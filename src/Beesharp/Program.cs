using Beesharp;

// Standalone Beesharp host (brain 0054, 0058): delegates wiring, endpoints, embedded Blazor UI,
// default plan loading, and headless CLI run mode to Beesharp.Host.
var builder = BeesharpApp.CreateBuilder(args);
var app = builder.Build();
return await app.RunAsync();
