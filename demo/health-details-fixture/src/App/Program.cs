var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Text("Healthy", "text/plain"));

app.Run();
