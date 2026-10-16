// Minimal workload for the CI IIS sites: "/" answers, "/alloc" allocates so the GC counters move.
var app = WebApplication.CreateBuilder(args).Build();
app.MapGet("/", () => "ok");
app.MapGet("/alloc", () =>
{
    var keep = new List<byte[]>();
    for (var i = 0; i < 200; i++)
    {
        keep.Add(new byte[100_000]);
    }

    return keep.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
});
app.Run();
