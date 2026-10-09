# Retained comparison performance check

Same-input synthetic stress workload:30 byte-distinct FFV1 recordings,1879frames
each;56370sampledframes and90continuousendpoint rows pertarget. This is fully
sampled numerical comparison stress, not tracking speed or real accuracy/animalN.
One retained baseline and one final candidate sample on this developer host.
Other engineering jobs were active; timings are observations, not a stable ratio.

| Comparison only | Baseline | Final G11 candidate | Managed allocation baseline/candidate |
|---|---:|---:|---:|
|Raw automatic|1377.487s|23.502s|118.765GB /0.417GB|
|Inspected corrected|2455.365s|11.362s|214.194GB /0.447GB|

Both final reports match independent constructed projections and are BYTEIDENTICAL
to baseline. Raw SHA2563C1A6FCBBEB00EA86B9648FE5103D77FB958B8DF2917C879F093F9503AF469B6;
corrected4EDB1EFA56024A954EE3D00031DFFDD886106FE3CCCBAB6E399354EF5D50E57F.

Finalmid-cancellation after50ms request: rawrequest53.161ms then54.986ms latency;
correctedrequest63.974ms then27.412ms latency. Pre-cancelled calls return without
fingerprinting/report in2.622ms raw and0.171ms corrected. No partial report is
published. Input deserialization is measured separately and precedes Compare.

Processlifetime peakworking set: baseline2.519GB, intermediate indexed-only
candidate3.448GB, final streaming-fingerprint candidate2.421GB. This includes
input/warmup/observer phases and is not a managedheap peak or a general lower-RAM
claim. A large individual JSON string can still require one writer span.

InputmanifestSHA256 E165BF198486A44A4FAB5F0CE92F810B8AD932D818773CBE47CA82C64EB28784.
Baseline pinAC35A3D473A233185F0AA74C7E3191E0F346B31AA697D5567679F03E85A559F6.
Finalrunner pin087F0F19C1F1B85B4C7E530AE3EC05E5C1D540D56106D294AB6179EB444AFA3B,
source273c725, including accepted G08/G11 and projecthelper changes. LaterDesktop
protocol-preset/camera-test changes do not change runner dependency/source trees.
Executingbinary pin is separate from frozen synthetic comparator identity.

Controller artifacts/benchmarks retain full-baseline-01, full-candidate-01 and
full-candidate-g11-01 plus pre/mid cancellation folders, started/completed/phase
receipts, original inputs, hashes and raw report JSON. All failures/intermediate
results remain. These large developer artifacts are not embedded in the portable
package. The corresponding source includes committed tests and documentation.