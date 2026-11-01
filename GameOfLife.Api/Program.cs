using GameOfLife.Api.Application;
using GameOfLife.Api.Endpoints;
using GameOfLife.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<BoardStateCodec>();
builder.Services.AddSingleton(services => new SqliteBoardStore(
    services.GetRequiredService<IConfiguration>().GetConnectionString("Boards")
        ?? "Data Source=boards.db",
    services.GetRequiredService<BoardStateCodec>()));
builder.Services.AddScoped<BoardService>();
var app = builder.Build();

app.Services.GetRequiredService<SqliteBoardStore>().Initialize();
app.MapBoardEndpoints();

app.Run();

public partial class Program { }
