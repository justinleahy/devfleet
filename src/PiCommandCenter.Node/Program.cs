using Microsoft.Extensions.Hosting;
using PiCommandCenter.Node;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPiNode();
builder.Build().Run();
