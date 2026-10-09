using MouseTrackerTabajara.Validation.Tests;
using Xunit.Abstractions;

if(args.Length!=1)throw new ArgumentException("Synthetic native project probe requires one mode.");
var tests=new ProcessedProjectPersistenceTests(new ProjectProbeOutput());
if(args[0]=="lifecycle")tests.NativeProjectActionsGuardReviewAndBusyThenReopenMissingSourceSafely();
else if(args[0]=="edit")tests.ImmediateNativeGridEditSaveOpenIncludesLastAnimalGroupAndTrial();
else throw new ArgumentException("Unknown project probe mode.");

sealed class ProjectProbeOutput:ITestOutputHelper
{
    public void WriteLine(string message)=>Console.WriteLine(message);
    public void WriteLine(string format,params object[] args)=>Console.WriteLine(format,args);
}
