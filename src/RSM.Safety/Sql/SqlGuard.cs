using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace RSM.Safety.Sql;

/// <summary>Which rule set to apply.</summary>
public enum SqlGuardProfile
{
    /// <summary>RSM's own embedded query catalog.</summary>
    Catalog,

    /// <summary>User-supplied query packs: stricter (tables/views only, no EXEC, no cross-database names, no UDFs).</summary>
    QueryPack,
}

public sealed record SqlGuardViolation(string Rule, string Message, int Line, int Column, string? Fragment);

public sealed record SqlGuardVerdict(bool IsAllowed, IReadOnlyList<SqlGuardViolation> Violations)
{
    public static readonly SqlGuardVerdict Allowed = new(true, []);

    public override string ToString() =>
        IsAllowed ? "allowed" : string.Join("; ", Violations.Select(v => $"{v.Rule} at {v.Line}:{v.Column}: {v.Message}"));
}

/// <summary>
/// Parses T-SQL with ScriptDom (TSql160Parser) and allows only read-only batches (ARCHITECTURE.md §6.2).
/// Fail-closed: parse errors, multiple batches and any statement type not on the allow-list are rejected;
/// table sources must be one of the known read-only kinds; dangerous fragments are rejected wherever they appear.
/// </summary>
public static class SqlGuard
{
    /// <summary>The only procedure RSM may EXECUTE.</summary>
    public const string ErrorLogProcedure = "xp_readerrorlog";

    private static readonly HashSet<string> XmlMethods = new(StringComparer.OrdinalIgnoreCase) { "value", "query", "exist", "nodes" };

    /// <summary>Unqualified table-valued functions allowed in catalog queries.</summary>
    private static readonly HashSet<string> UnqualifiedTvfs = new(StringComparer.OrdinalIgnoreCase)
    {
        "fn_my_permissions", "fn_builtin_permissions", "fn_helpcollations",
    };

    /// <summary>dbo objects (msdb, ReportServer) the catalog may read. Everything else must live in sys / INFORMATION_SCHEMA.</summary>
    private static readonly HashSet<string> AllowedDboObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        // msdb
        "backupset", "backupmediafamily", "backupfile", "sysjobs", "sysjobsteps", "sysjobhistory", "sysjobactivity",
        "syssessions", "sysschedules", "sysjobschedules", "syscategories", "suspect_pages", "sysmaintplan_plans",
        "sysmaintplan_subplans", "cdc_jobs", "log_shipping_monitor_primary", "log_shipping_monitor_secondary",
        // ReportServer
        "ExecutionLog3", "ExecutionLogStorage", "Catalog", "Subscriptions",
    };

    private static readonly HashSet<string> AllowedDatabases = new(StringComparer.OrdinalIgnoreCase) { "master", "msdb", "tempdb" };

    public static SqlGuardVerdict Check(string sql, SqlGuardProfile profile = SqlGuardProfile.Catalog)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        TSqlFragment? fragment;
        IList<ParseError> errors;
        using (var reader = new StringReader(sql))
        {
            fragment = parser.Parse(reader, out errors);
        }

        if (errors.Count > 0 || fragment is not TSqlScript script)
        {
            return new SqlGuardVerdict(false, errors
                .Select(e => new SqlGuardViolation("SG001-parse", e.Message, e.Line, e.Column, null))
                .DefaultIfEmpty(new SqlGuardViolation("SG001-parse", "not a T-SQL script", 0, 0, null))
                .ToList());
        }

        if (script.Batches.Count != 1)
        {
            return new SqlGuardVerdict(false, [new SqlGuardViolation("SG002-batches", "exactly one batch is allowed (no GO separators)", 1, 1, null)]);
        }

        // CTE names are visited after the query that uses them, so collect them up-front.
        var ctes = new CteCollector();
        script.Accept(ctes);

        var visitor = new GuardVisitor(profile, sql, ctes.Names);
        script.Accept(visitor);
        return visitor.Violations.Count == 0 ? SqlGuardVerdict.Allowed : new SqlGuardVerdict(false, visitor.Violations);
    }

    private sealed class CteCollector : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(CommonTableExpression node) => Names.Add(node.ExpressionName.Value);
    }

    private sealed class GuardVisitor(SqlGuardProfile profile, string sql, HashSet<string> cteNames) : TSqlFragmentVisitor
    {
        private readonly HashSet<string> _tableVariables = new(StringComparer.OrdinalIgnoreCase);

        public List<SqlGuardViolation> Violations { get; } = [];

        // Every node passes through here first (ScriptDom calls Visit(TSqlFragment) for all nodes).
        public override void Visit(TSqlFragment node)
        {
            switch (node)
            {
                case TSqlStatement statement:
                    CheckStatement(statement);
                    break;
                case TableReference table:
                    CheckTableReference(table);
                    break;
                case NextValueForExpression:
                    Reject("SG020-sequence", "NEXT VALUE FOR changes a sequence", node);
                    break;
                case SchemaObjectName name:
                    CheckName(name);
                    break;
                case TableHint hint when hint.HintKind is not (TableHintKind.NoLock or TableHintKind.ReadUncommitted or TableHintKind.NoWait):
                    Reject("SG021-table-hint", $"table hint {hint.HintKind} is not allowed (only NOLOCK/READUNCOMMITTED/NOWAIT)", node);
                    break;
                case OptimizerHint hint when hint.HintKind is not (OptimizerHintKind.Recompile or OptimizerHintKind.MaxDop):
                    Reject("SG022-query-hint", $"query hint {hint.HintKind} is not allowed (only RECOMPILE/MAXDOP)", node);
                    break;
                case ForClause forClause when forClause is not (XmlForClause or JsonForClause):
                    Reject("SG024-for-clause", "only FOR XML / FOR JSON are allowed", node);
                    break;
                case OutputClause or OutputIntoClause:
                    Reject("SG023-output", "OUTPUT clauses are not allowed", node);
                    break;
                case FunctionCall call:
                    CheckFunctionCall(call);
                    break;
            }
        }

        private void CheckStatement(TSqlStatement statement)
        {
            switch (statement)
            {
                case SelectStatement select:
                    if (select.Into is not null)
                    {
                        Reject("SG030-select-into", "SELECT … INTO creates a table", statement);
                    }

                    if (select.On is not null)
                    {
                        Reject("SG030-select-into", "SELECT … INTO … ON is not allowed", statement);
                    }

                    break;
                case DeclareTableVariableStatement declareTable:
                    _tableVariables.Add(declareTable.Body.VariableName.Value);
                    break;
                case DeclareVariableStatement declare:
                    foreach (var element in declare.Declarations)
                    {
                        var typeName = element.DataType?.Name?.BaseIdentifier?.Value;
                        if (element.DataType is null
                            || element.DataType is SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Cursor }
                            || string.Equals(typeName, "cursor", StringComparison.OrdinalIgnoreCase))
                        {
                            Reject("SG031-cursor", "cursor variables are not allowed", element);
                        }
                    }

                    break;
                case SetVariableStatement set:
                    if (set.CursorDefinition is not null || set.FunctionCallExists)
                    {
                        Reject("SG032-set-variable", "only plain assignments to local variables are allowed", statement);
                    }

                    break;
                case InsertStatement insert:
                    CheckInsert(insert);
                    break;
                case SetTransactionIsolationLevelStatement isolation:
                    if (isolation.Level != IsolationLevel.ReadUncommitted)
                    {
                        Reject("SG033-session-set", "only READ UNCOMMITTED isolation is allowed", statement);
                    }

                    break;
                case PredicateSetStatement predicate:
                    if (predicate.Options != SetOptions.NoCount || !predicate.IsOn)
                    {
                        Reject("SG033-session-set", $"SET {predicate.Options} is not allowed (only SET NOCOUNT ON)", statement);
                    }

                    break;
                case SetCommandStatement command:
                    foreach (var c in command.Commands)
                    {
                        var ok = c is GeneralSetCommand g && (g.CommandType == GeneralSetCommandType.LockTimeout
                            || (g.CommandType == GeneralSetCommandType.DeadlockPriority && g.Parameter is IdentifierLiteral { Value: var v } && v.Equals("LOW", StringComparison.OrdinalIgnoreCase)));
                        if (!ok)
                        {
                            Reject("SG033-session-set", "only SET LOCK_TIMEOUT and SET DEADLOCK_PRIORITY LOW are allowed", c);
                        }
                    }

                    break;
                case ExecuteStatement execute:
                    CheckExecute(execute.ExecuteSpecification, statement);
                    break;
                case IfStatement or BeginEndBlockStatement:
                    // Containers only; their children are visited and checked individually.
                    break;
                default:
                    Reject("SG010-statement", $"{Describe(statement)} is not a read-only statement", statement);
                    break;
            }
        }

        private void CheckInsert(InsertStatement insert)
        {
            var spec = insert.InsertSpecification;
            if (spec.Target is not VariableTableReference target || !_tableVariables.Contains(target.Variable.Name))
            {
                Reject("SG040-insert-target", "INSERT is only allowed into a table variable declared earlier in the batch", insert);
            }

            switch (spec.InsertSource)
            {
                case SelectInsertSource or ValuesInsertSource:
                    break;
                case ExecuteInsertSource exec:
                    CheckExecute(exec.Execute, insert);
                    break;
                default:
                    Reject("SG041-insert-source", "unsupported INSERT source", insert);
                    break;
            }
        }

        private void CheckExecute(ExecuteSpecification spec, TSqlFragment at)
        {
            if (profile == SqlGuardProfile.QueryPack)
            {
                Reject("SG050-exec", "query packs may not execute procedures", at);
                return;
            }

            if (spec.ExecuteContext is not null || spec.LinkedServer is not null)
            {
                Reject("SG050-exec", "EXECUTE AS / AT linked server is not allowed", at);
            }

            if (spec.ExecutableEntity is not ExecutableProcedureReference proc
                || proc.ProcedureReference?.ProcedureReference?.Name is not { } name
                || !IsErrorLogProcedure(name))
            {
                Reject("SG050-exec", $"only EXEC master.sys.{ErrorLogProcedure} is allowed", at);
                return;
            }

            foreach (var parameter in proc.Parameters)
            {
                if (parameter.IsOutput || parameter.ParameterValue is not (Literal or VariableReference))
                {
                    Reject("SG051-exec-args", "procedure arguments must be literals or local variables", parameter);
                }
            }
        }

        private static bool IsErrorLogProcedure(SchemaObjectName name) =>
            name.ServerIdentifier is null
            && string.Equals(name.BaseIdentifier?.Value, ErrorLogProcedure, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(name.SchemaIdentifier?.Value) || string.Equals(name.SchemaIdentifier.Value, "sys", StringComparison.OrdinalIgnoreCase))
            && (name.DatabaseIdentifier is null || string.Equals(name.DatabaseIdentifier.Value, "master", StringComparison.OrdinalIgnoreCase));

        private void CheckTableReference(TableReference table)
        {
            switch (table)
            {
                case NamedTableReference named:
                    CheckNamedObject(named.SchemaObject, named);
                    break;
                case SchemaObjectFunctionTableReference tvf:
                    CheckTableValuedFunction(tvf);
                    break;
                case VariableTableReference variable:
                    if (!_tableVariables.Contains(variable.Variable.Name))
                    {
                        Reject("SG060-table-variable", $"table variable {variable.Variable.Name} is not declared in this batch", table);
                    }

                    break;
                case QueryDerivedTable or InlineDerivedTable or JoinTableReference or JoinParenthesisTableReference
                    or GlobalFunctionTableReference or OpenJsonTableReference or OpenXmlTableReference
                    or VariableMethodCallTableReference or PivotedTableReference or UnpivotedTableReference:
                    break;
                default:
                    // OPENROWSET, OPENQUERY, OPENDATASOURCE, BULK, full-text, change tables, …
                    Reject("SG061-table-source", $"{Describe(table)} is not an allowed table source", table);
                    break;
            }
        }

        private void CheckTableValuedFunction(SchemaObjectFunctionTableReference tvf)
        {
            var name = tvf.SchemaObject;
            var baseName = name.BaseIdentifier?.Value;

            // xmlColumn.nodes('…') parses as a function table reference.
            if (baseName is not null && baseName.Equals("nodes", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (profile == SqlGuardProfile.QueryPack)
            {
                Reject("SG062-function", "query packs may reference tables and views only", tvf);
                return;
            }

            var schema = name.SchemaIdentifier?.Value;
            var ok = schema is null
                ? baseName is not null && UnqualifiedTvfs.Contains(baseName)
                : schema.Equals("sys", StringComparison.OrdinalIgnoreCase) && IsAllowedDatabase(name);
            if (!ok)
            {
                Reject("SG062-function", $"table-valued function {Join(name)} is not allowed", tvf);
            }
        }

        private void CheckNamedObject(SchemaObjectName name, TSqlFragment at)
        {
            var schema = name.SchemaIdentifier?.Value;
            var baseName = name.BaseIdentifier?.Value ?? string.Empty;

            if (schema is null && name.DatabaseIdentifier is null && cteNames.Contains(baseName))
            {
                return;
            }

            if (profile == SqlGuardProfile.QueryPack)
            {
                if (name.DatabaseIdentifier is not null)
                {
                    Reject("SG063-object", "query packs may not use cross-database names", at);
                }

                return; // tables/views of the target DB
            }

            var ok = schema switch
            {
                null => false,
                _ when schema.Equals("sys", StringComparison.OrdinalIgnoreCase) || schema.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase)
                    => IsAllowedDatabase(name),
                _ when schema.Equals("dbo", StringComparison.OrdinalIgnoreCase)
                    => AllowedDboObjects.Contains(baseName) && (name.DatabaseIdentifier is null || name.DatabaseIdentifier.Value.Equals("msdb", StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };

            if (!ok)
            {
                Reject("SG063-object", $"object {Join(name)} is not on the catalog allow-list (sys.*, INFORMATION_SCHEMA.*, listed msdb/ReportServer tables)", at);
            }
        }

        private static bool IsAllowedDatabase(SchemaObjectName name) =>
            name.DatabaseIdentifier is null || AllowedDatabases.Contains(name.DatabaseIdentifier.Value);

        private void CheckName(SchemaObjectName name)
        {
            if (name.ServerIdentifier is not null)
            {
                Reject("SG064-linked-server", "four-part (linked server) names are not allowed", name);
            }

            if (name.Identifiers.Any(i => i.Value.StartsWith('#')))
            {
                Reject("SG065-temp-table", "#temp / ##temp tables are not allowed", name);
            }
        }

        private void CheckFunctionCall(FunctionCall call)
        {
            var functionName = call.FunctionName.Value;
            if (call.CallTarget is null)
            {
                return; // built-in function
            }

            if (functionName.Equals("modify", StringComparison.OrdinalIgnoreCase))
            {
                Reject("SG066-xml-modify", "XML modify() is not allowed", call);
                return;
            }

            if (XmlMethods.Contains(functionName))
            {
                return; // column.value('…'), x.query('…') …
            }

            var isSys = call.CallTarget is MultiPartIdentifierCallTarget target
                && target.MultiPartIdentifier.Identifiers.Count >= 1
                && target.MultiPartIdentifier.Identifiers[^1].Value.Equals("sys", StringComparison.OrdinalIgnoreCase);
            if (!isSys || profile == SqlGuardProfile.QueryPack)
            {
                Reject("SG067-udf", $"user-defined function call {functionName}() is not allowed", call);
            }
        }

        private void Reject(string rule, string message, TSqlFragment at)
        {
            string? text = null;
            if (at.StartOffset >= 0 && at.FragmentLength > 0 && at.StartOffset + at.FragmentLength <= sql.Length)
            {
                text = sql.Substring(at.StartOffset, Math.Min(at.FragmentLength, 120));
            }

            Violations.Add(new SqlGuardViolation(rule, message, at.StartLine, at.StartColumn, text));
        }

        private static string Describe(TSqlFragment fragment) => fragment.GetType().Name;

        private static string Join(SchemaObjectName name) => string.Join('.', name.Identifiers.Select(i => i.Value));
    }
}
