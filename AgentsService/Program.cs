var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// بيانات افتراضية (في تطبيق حقيقي دي هتكون في DB)
var agents = new List<Agent>
{
    new("007", "Cobra",   "Infiltration", "Active",  "Cairo",   47),
    new("008", "Viper",   "Sniping",      "Active",  "London",  32),
    new("009", "Falcon",  "Cyber",        "Active",  "Berlin",  28),
    new("010", "Ghost",   "Surveillance", "Retired", "Lisbon",  89),
    new("011", "Tiger",   "Combat",       "Active",  "Tokyo",   15),
    new("012", "Phoenix", "Demolition",   "Missing", "Unknown", 41),
    new("013", "Shadow",  "Stealth",      "Active",  "Dubai",   23),
    new("014", "Storm",   "Infiltration", "Active",  "Cairo",   19),
};

// Endpoints
app.MapGet("/agents", () => agents);

app.MapGet("/agents/{id}", (string id) =>
{
    var agent = agents.FirstOrDefault(a => a.Id == id);
    return agent is null ? Results.NotFound() : Results.Ok(agent);
});

app.MapGet("/agents/active", () =>
    agents.Where(a => a.Status == "Active"));

app.MapGet("/agents/by-specialty/{specialty}", (string specialty) =>
    agents.Where(a => a.Specialty.Equals(specialty, StringComparison.OrdinalIgnoreCase)));

app.MapGet("/agents/by-location/{location}", (string location) =>
    agents.Where(a => a.Location.Equals(location, StringComparison.OrdinalIgnoreCase)));

app.Run();

record Agent(
    string Id,
    string Codename,
    string Specialty,
    string Status,
    string Location,
    int MissionsCompleted
);