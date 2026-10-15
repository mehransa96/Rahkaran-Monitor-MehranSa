-- key:         sql.sec.sa_blank_pwd
-- purpose:     Whether sa has an empty password (NULL = cannot tell: needs CONTROL SERVER)
-- min_version: 13
-- permission:  CONTROL SERVER (optional)
-- cost:        cheap
-- timeout_s:   10
-- database:    master
SELECT CASE WHEN sl.password_hash IS NULL THEN NULL
            WHEN PWDCOMPARE(N'', sl.password_hash) = 1 THEN 1
            ELSE 0 END AS BlankPassword
FROM sys.sql_logins AS sl
WHERE sl.sid = 0x01;
