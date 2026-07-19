using System.Text.Json.Serialization;
using FenceDesk.Services;

namespace FenceDesk.Models;

public sealed class FenceLayout
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("settings")]
    public AppSettings Settings { get; set; } = new();

    [JsonPropertyName("fences")]
    [JsonConverter(typeof(FlexibleListConverter<FenceModel>))]
    public List<FenceModel> Fences { get; set; } = new();
}

public sealed class AppSettings
{
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("doubleClickDesktopHide")]
    public bool DoubleClickDesktopHide { get; set; }

    [JsonPropertyName("defaultOpacity")]
    public double DefaultOpacity { get; set; } = 0.72;

    [JsonPropertyName("accentColor")]
    public string AccentColor { get; set; } = "#0F1724";

    [JsonPropertyName("showFences")]
    public bool ShowFences { get; set; } = true;
}

public sealed class FenceModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("title")]
    public string Title { get; set; } = "New Fence";

    [JsonPropertyName("x")]
    public double X { get; set; } = 100;

    [JsonPropertyName("y")]
    public double Y { get; set; } = 100;

    [JsonPropertyName("width")]
    public double Width { get; set; } = 360;

    [JsonPropertyName("height")]
    public double Height { get; set; } = 200;

    [JsonPropertyName("rolledUp")]
    public bool RolledUp { get; set; }

    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 0.72;

    [JsonPropertyName("bgColor")]
    public string BgColor { get; set; } = "#0F1724";

    /// <summary>"items" or "portal"</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "items";

    [JsonPropertyName("activeTabId")]
    public string ActiveTabId { get; set; } = string.Empty;

    [JsonPropertyName("portalPath")]
    public string? PortalPath { get; set; }

    [JsonPropertyName("groupId")]
    public string? GroupId { get; set; }

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    [JsonPropertyName("tabs")]
    [JsonConverter(typeof(FlexibleListConverter<FenceTab>))]
    public List<FenceTab> Tabs { get; set; } = new();

    [JsonIgnore]
    public bool IsPortal => string.Equals(Mode, "portal", StringComparison.OrdinalIgnoreCase);

    public void EnsureDefaults()
    {
        if (string.IsNullOrWhiteSpace(Id))
            Id = Guid.NewGuid().ToString();
        if (string.IsNullOrWhiteSpace(Title))
            Title = "New Fence";
        if (string.IsNullOrWhiteSpace(BgColor))
            BgColor = "#0F1724";
        if (Opacity is < 0 or > 1)
            Opacity = Math.Clamp(Opacity, 0, 1);
        if (Width < 140) Width = 140;
        if (Height < 40) Height = 80;
        if (Tabs.Count == 0)
        {
            var tabId = Guid.NewGuid().ToString();
            Tabs.Add(new FenceTab { Id = tabId, Title = Title });
            ActiveTabId = tabId;
        }
        if (string.IsNullOrWhiteSpace(ActiveTabId) || Tabs.All(t => t.Id != ActiveTabId))
            ActiveTabId = Tabs[0].Id;
        if (string.IsNullOrWhiteSpace(Mode))
            Mode = "items";
    }

    public FenceTab? GetActiveTab()
    {
        EnsureDefaults();
        return Tabs.FirstOrDefault(t => t.Id == ActiveTabId) ?? Tabs.FirstOrDefault();
    }

    public static FenceModel Create(
        string title = "New Fence",
        string mode = "items",
        string? portalPath = null,
        double x = 100,
        double y = 100,
        double width = 360,
        double height = 200)
    {
        var tabId = Guid.NewGuid().ToString();
        return new FenceModel
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Mode = mode,
            PortalPath = portalPath,
            ActiveTabId = tabId,
            Tabs = new List<FenceTab>
            {
                new() { Id = tabId, Title = title }
            }
        };
    }
}

public sealed class FenceTab
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("title")]
    public string Title { get; set; } = "Items";

    [JsonPropertyName("items")]
    [JsonConverter(typeof(FlexibleListConverter<FenceItem>))]
    public List<FenceItem> Items { get; set; } = new();
}

public sealed class FenceItem
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

public sealed class DesktopHiddenState
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonPropertyName("paths")]
    public List<string> Paths { get; set; } = new();

    [JsonPropertyName("shelved")]
    public Dictionary<string, string> Shelved { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("shellIcons")]
    public List<string> ShellIcons { get; set; } = new();
}
