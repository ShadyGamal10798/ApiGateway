var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

var missions = new List<Mission>
{
    new("M-2024-001", "Operation Nile",  "Cairo",   "High",     "InProgress", "007", "2024-12-31"),
    new("M-2024-002", "Silent Storm",    "London",  "Medium",   "InProgress", "008", "2024-11-15"),
    new("M-2024-003", "Digital Phantom", "Berlin",  "Critical", "InProgress", "009", "2024-12-01"),
    new("M-2024-004", "Desert Wind",     "Dubai",   "Low",      "Completed",  "013", "2024-10-30"),
    new("M-2024-005", "Black Tide",      "Tokyo",   "High",     "Pending",    "011", "2025-01-15"),
    new("M-2024-006", "Lost Echo",       "Unknown", "Critical", "Failed",     "012", "2024-09-30"),
};

app.MapGet("/missions", () => missions);

app.MapGet("/missions/{id}", (string id) =>
{
    var mission = missions.FirstOrDefault(m => m.Id == id);
    return mission is null ? Results.NotFound() : Results.Ok(mission);
});

app.MapGet("/missions/in-progress", () =>
    missions.Where(m => m.Status == "InProgress"));

app.MapGet("/missions/by-agent/{agentId}", (string agentId) =>
    missions.Where(m => m.AssignedAgentId == agentId));

app.MapGet("/missions/by-difficulty/{difficulty}", (string difficulty) =>
    missions.Where(m => m.Difficulty.Equals(difficulty, StringComparison.OrdinalIgnoreCase)));

app.Run();

record Mission(
    string Id,
    string Name,
    string TargetCity,
    string Difficulty,
    string Status,
    string AssignedAgentId,
    string Deadline
);