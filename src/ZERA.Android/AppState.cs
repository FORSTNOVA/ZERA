using System.Text.Json.Serialization;
using ZERA.Core.Download;
using ZERA.Core.Panels;

namespace ZERA.Android;

internal sealed class SavedAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Platform { get; set; }
    public string Name { get; set; }
    public string Credential { get; set; }
}
internal sealed class MobileSettings
{
    public int Threads { get; set; } = 8;
    public int Concurrent { get; set; } = 2;
    public string WriteMode { get; set; } = "SharedFile";
    public string Theme { get; set; } = "system";
    public bool AutoExport { get; set; }
    public bool FloatingWindow { get; set; }
    public string ExportTree { get; set; }
    public string ExportTreeName { get; set; }
}
internal sealed class ExportRecord
{
    public string State { get; set; }
    public string Tree { get; set; }
    public string Uri { get; set; }
    public string Error { get; set; }
    public int Files { get; set; }
}
internal sealed class SavedTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; }
    public string State { get; set; } = "Paused";
    public string AccountId { get; set; }
    public int Platform { get; set; }
    public string Path { get; set; }
    public string DirectUrl { get; set; }
    public string ShareUrl { get; set; }
    public string Passcode { get; set; }
    public PendingInfo Pending { get; set; }
    public long Done { get; set; }
    public long Total { get; set; }
    public string Error { get; set; }
    [JsonIgnore] public double Speed { get; set; }
    [JsonIgnore] public DownloadJob Job { get; set; }
    [JsonIgnore] public bool Running { get; set; }
    public string WriteMode { get; set; } = "SharedFile";
}
internal sealed class AppState
{
    public int Platform { get; set; }
    public string ActiveAccount { get; set; }
    public Dictionary<int, string> LastAccounts { get; set; } = new();
    public List<SavedAccount> Accounts { get; set; } = new();
    public List<SavedTask> Tasks { get; set; } = new();
    public MobileSettings Settings { get; set; } = new();
    public Dictionary<string, ExportRecord> Exports { get; set; } = new();
}
