using Kestrel.Transport.IoUring;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseIoUring(options =>
{
    options.RingSize = 256;
});

var app = builder.Build();
app.MapGet("/", () => "Hello from io_uring!");
app.MapGet("/health", () => Results.Ok(new { status = "healthy", transport = "io_uring" }));
app.Run();
