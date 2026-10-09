using System.IO;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Desktop;
if (args.Length != 3 || args[0] != "publication-abrupt-exit") return 2;
var report = JsonDefaults.Deserialize<StudyReportDocument>(File.ReadAllText(args[2]));
StudyReportPublication.Publish(args[1], report, null, afterPayload: name => { if (name == "report.json") Environment.Exit(77); });
return 3;
public sealed class ReportCrashMarker;
