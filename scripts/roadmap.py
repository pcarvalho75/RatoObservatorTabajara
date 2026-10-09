"""Generate the fixed 1,000-point roadmap and recompute evidence-based progress."""
import html
import json
from datetime import datetime,timezone,timedelta
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
PLAN=ROOT/'docs/ROADMAP.json'

def initial_plan():
    phases=[
      ('A','Foundation and source contracts',[
        ('A1','Audit source, license and preserved reference',15,.5,1,[],'Freeze DORIS commit, Python version, video hash and provenance. Keep GPL notices and a development/validation distinction.','Source inventory and provenance file match the actual checkout.'),
        ('A2','Create the .NET 10 WPF solution and pin packages',20,1,2,['A1'],'Create Core, Tracking, Storage, Desktop, CLI and Tests projects; pin managed/native OpenCV together and produce lockfiles.','Release build passes with zero warnings and native OpenCV loads.'),
        ('A3','Define typed study, frame and measurement contracts',25,1.5,3,['A1'],'Use explicit mouse, group, phase, day, trial and source identity. Define zero-based frame IDs, timestamps, point status, geometry, uncertainty and run versions.','Contracts serialize/round-trip and invalid settings are rejected.'),
        ('A4','Implement SQLite persistence and immutable runs',25,2,4,['A3'],'Store assets, analysis runs, correction overlays, reviews and audit records transactionally. Retain all completed runs and separate automatic data from edits.','Reopening preserves metadata; raw runs remain unchanged after corrections; duplicate content is detected.'),
        ('A5','Freeze Python fixtures and reference coordinates',15,1,2,['A1','A3'],'Export numerical edge cases and all 1,879 source-video frame rows. Store input hashes and provenance; label these development fixtures.','Fixtures can be regenerated and verify the same original source hash.'),
        ('A6','Publish scored roadmap and acceptance ledger',20,.5,1,['A1'],'Maintain this JSON, readable HTML/TXT, per-step evidence, hour budgets and fixed point weights. Distinguish implemented, verified and pending.','Weights sum to exactly 1,000; score recomputes solely from accepted completed steps.')]),
      ('B','Native detector and safe measurements',[
        ('B1','Read video metadata and verify frame timing',30,1.5,3,['A2','A3'],'Decode with native FFmpeg/OpenCV. Retain presentation timestamps and canonical frame identity; fail closed when initial CFR semantics cannot be verified.','Known CFR frames agree with presentation timestamps; irregular timing is explicitly withheld.'),
        ('B2','Port grayscale, blur, threshold, inversion and arena',25,1,2,['B1','A5'],'Reproduce the working detector with the same image operations, border behavior and 160-point arena. Dispose native buffers.','Frozen image masks agree pixel-by-pixel with the reference settings.'),
        ('B3','Port contours, object filters and centroids',30,1,2,['B2'],'Use external contours, moment centroids, area range and maximum coordinate extent. Preserve rounding/truncation behavior.','Synthetic and original-frame detections reproduce known positions and filter boundaries.'),
        ('B4','Correct exclusion-mask polarity',20,1,2,['B2'],'Apply exclusions after threshold/inversion; prevent dark exclusions from becoming foreground. Validate normal and inverted polarity.','Excluded pixels remain background for both polarity settings.'),
        ('B5','Implement unknown, ambiguous and implausible-step states',25,1.5,3,['B3'],'Require single-object evidence; record tentative candidates separately. Reject implausible adjacent motion and never interpolate or bridge unknown gaps.','Ambiguity/gap/jump tests preserve unknown time and prevent invented displacement.'),
        ('B6','Port spatial measurements, physical units and bounds',25,3,6,['B5','A5'],'Compute adjacent-pair path/speed, exclusive quadrants, wall time, proximity and site crossings. Retain whole-trial occupancy bounds and observed-time denominators.','Numerical reference cases pass with declared tolerance; quadrant plus unknown time equals trial duration.'),
        ('B7','Port endpoint-specific event-history safeguards',25,2,4,['B6'],'Distinguish exact observed first entry, complete no-entry censoring and histories with possible hidden visits. Withhold unsupported scalar endpoints.','A gap before a visit cannot create a false exact event; a gap after a known first visit does not invalidate its prior timing.')]),
      ('C','WPF review and annotation workflow',[
        ('C1','Build the native WPF workspace and typed trial table',25,2,4,['A2','A3','A4'],'Create navigation, import actions, study table, live status and quality readouts with typed bindings and accessible focus.','The actual WPF application loads without binding/startup errors and renders a native study screen.'),
        ('C2','Synchronize frame viewing and trajectory overlays',25,2,4,['C1','B1','B5'],'Seek and step by canonical frame ID. Display the same pool, platform and accepted points used by analysis; show gaps without connecting lines.','A rendered known frame aligns its overlay with decoded geometry; seek/step identity is checked.'),
        ('C3','Add pool, platform and calibration editing',25,3,6,['C2','B6'],'Provide pool bounds, platform center/size, physical diameter and confirmed geometry. Persist edits and invalidate incompatible prior review.','Pointer editing is verified at different display sizes/DPI; saved geometry reproduces the same measurement transform.'),
        ('C4','Add manual corrections and biological event annotations',25,3,6,['C2','A4','B7'],'Correct or exclude exact frames as overlays. Separately record water entry, platform mounting, intervention and recording end with reasons.','Corrections preserve raw data, recompute derived metrics and invalidate review; semantic events cannot be confused with region entry.'),
        ('C5','Implement blinded review and pseudonymous identities',25,2,4,['C1','A4'],'Hide group labels, source filenames and identifying animal IDs during review. Retain stable aliases and an explicit unblinding record.','Blinded screens and review exports reveal neither treatment labels nor informative filenames.'),
        ('C6','Rank review moments by consequence and add random audits',25,3,6,['C2','B7'],'Prioritize uncertain event histories, suspicious crossings, contour changes and persistent detections; add deterministic random apparently-good segments.','Queue includes critical failures and random audits, with reasons and direct frame navigation; ranking is treatment-blind.'),
        ('C7','Record reviewed status and reasoned inclusion/exclusion',20,2,4,['C4','C5'],'Record inspection separately from endpoint eligibility. Require stated exclusion reasons and surface stale geometry/settings/source changes.','Coverage alone cannot approve accuracy or alter the biological trial window; exclusions remain visible.')]),
      ('D','Batch processing and protocol management',[
        ('D1','Import multiple videos and provisional mouse identities',25,1.5,3,['C1','A4'],'Import approximately 30 recordings with independent editable mouse IDs, groups, phases and trials. Detect duplicate bytes and moved sources.','Multi-import persists all unique recordings; duplicated content cannot count as a new mouse.'),
        ('D2','Implement cancelable, restartable serial batch queue',15,2,4,['D1','B7'],'Run off the UI thread, preserve finished clips, cancel safely and reuse only matching source/settings runs. Resume at the video level.','Thirty synthetic clips complete; cancellation retains completed runs and a later queue resumes safely.'),
        ('D3','Define training/probe windows and confirmed onset rules',30,2,4,['D1','C4'],'Record protocol-defined release and termination. Suggest onset only for confirmation; support fixed probe exposure and outcome-dependent training termination.','Unequal probe windows are flagged; windows cannot be shortened merely to pass tracking criteria.'),
        ('D4','Apply camera-specific presets with audit and invalidation',15,1,2,['C3','D3'],'Save/reuse detector and geometry profiles with camera/resolution identity. Confirm changed geometry and invalidate stale results.','Applying a profile never silently changes trial outcomes or accepts incompatible camera settings.'),
        ('D5','Handle failures, interrupted jobs and source integrity',25,2,4,['D2','A4'],'Record decoder errors and cancellations per clip; keep previous success intact. Verify input hashes before/after processing and recover after restart.','A failed or modified clip cannot publish a partial completed report or erase earlier evidence.')]),
      ('E','Animal atlas and temporal exploration',[
        ('E1','Build the individual trajectory and playback atlas',35,2,4,['C2','D2'],'Show each mouse with its path, review state and source video; allow synchronized playback without hiding individual heterogeneity.','A visible group pattern can be traced to every contributing animal, including outliers.'),
        ('E2','Build common-scale, animal-equal occupancy comparisons',35,3,6,['E1','D3'],'Use common physical/normalized geometry, matched planned probe time and shared color scales. Average trials within mouse before group maps.','Duplicating a mouse trial cannot increase that mouse\'s group-map weight; unknown mass and contributing animals are explicit.'),
        ('E3','Build prespecified temporal profiles and across-day views',25,3,6,['B6','D3','E1'],'Plot proximity, occupancy, speed and wall behavior in planned bins. Distinguish within-animal learning from independent cohorts across days.','Plots retain mouse identity and exposure; censored first visits are not averaged as successful visits.'),
        ('E4','Add measured calibration and camera-shift diagnostics',15,2,4,['C3','D4'],'Support measured water-plane landmarks and check camera displacement between clips. Preserve room and target-aligned orientations separately.','Known shifts/calibration errors are detected; unsupported projective assumptions are labeled.')]),
      ('F','Robustness and uncertainty exploration',[
        ('F1','Show occupancy and group-difference uncertainty bounds',20,1,2,['B6','E2'],'Propagate unknown time to per-mouse occupancy bounds and conservative group-difference bounds, conditional on correct accepted positions.','Bounds widen with missing data, include known values and are not labeled confidence intervals.'),
        ('F2','Replay documented detector-setting scenarios',35,3,6,['D4','D5'],'Run prespecified plausible threshold/blur/size scenarios as versioned analyses; retain every scenario and execution failure.','Original analysis remains frozen; replay yields reproducible endpoint changes without selecting only favorable scenarios.'),
        ('F3','Replay platform/calibration uncertainty scenarios',25,3,6,['F1','E4'],'Vary measured region placement and geometry within documented plausible ranges; compare endpoint consequences.','Region/calibration scenario envelopes are distinct from sampling uncertainty and use disclosed ranges.'),
        ('F4','Produce an interpretable result-stability report',20,2,4,['F2','F3'],'Show which conclusions change direction or remain unsupported under scenarios, and identify animals/errors driving the change.','No causal memory score is invented; stable and unstable results link to source evidence.')]),
      ('G','Scientific design, regression and validation',[
        ('G1','Confirm study design and freeze primary comparisons',20,1,2,['D1','D3'],'Confirm unique animals, group allocation, cage/litter clustering, repeated trials and planned endpoints with Luciana. Label exploratory analysis explicitly.','A recorded design supports the experimental unit and primary comparisons; filename identity is not treated as confirmation.'),
        ('G2','Audit animal weights, units, windows and endpoint holds',25,2,4,['G1','B7','E2'],'Prevent duplicate/trial/group conflicts, mixed units, unequal probe exposure and silent complete-case endpoint selection.','Every included and held endpoint has an explicit denominator and reason; repeated videos cannot inflate biological N.'),
        ('G3','Verify C# masks, frame rows and numerical regression',35,2,4,['A5','B7','D2'],'Compare native masks, all original-video frame/status/coordinate rows and metric fixtures. Exercise storage, cancellation and export failure boundaries.','Coordinate joins and declared numerical tolerances pass; regression is labeled separately from accuracy validation.'),
        ('G4','Validate on held-out real videos with manual reference',40,8,20,['G1','G3'],'Obtain remaining videos; separate tuning and validation by recording/session. Annotate random frames and difficult continuous intervals; second-review a subset.','Prespecified position/event/endpoint tolerances pass on held-out data; unresolved endpoint-critical ambiguity is disclosed.'),
        ('G5','Audit failure/exclusion bias and interpretation limits',20,2,4,['G2','G4','F4'],'Inspect tracking errors, uncertainty and exclusions by group after blinded review. Examine spatial endpoints together with motor/visible-platform context.','Differential failures cannot silently create a group result; scientific conclusions stay within validated measurements.')]),
      ('H','Windows delivery and reproducible evidence',[
        ('H1','Provide developer launch and native smoke checks',10,1,2,['C1','C2'],'Build a usable WPF executable and launcher; run an unattended startup/render check and save diagnostics.','Native startup succeeds and a real-data WPF screenshot is saved.'),
        ('H2','Publish self-contained Windows x64 distribution',20,1.5,3,['H1','D5'],'Package the app/runtime/native libraries, license and instructions without a Python dependency. Test deployment on a clean Windows environment.','A clean Windows machine opens a study and analyzes a known recording without developer tools.'),
        ('H3','Package installer/update path and release identity',15,2,4,['H2'],'Provide versioned installation or portable ZIP and documented update/rollback. Add signing when a trusted certificate is available.','Upgrading preserves projects and can be rolled back; release provenance and licenses accompany binaries.'),
        ('H4','Finish replayable reports, recovery and usability validation',25,2,4,['G5','H2'],'Freeze a reproducible report with source hashes, protocol, edits, exclusions and version. Test crash recovery and usability with Luciana.','The release report reproduces from saved inputs, failures recover, and researcher sign-off is recorded.')])
    ]
    return dict(product='Mouse Tracker Tabajara native WPF for Windows',version=1,total_points=1000,
        scoring='Only a completed step with acceptance evidence earns its fixed points. In-progress code earns zero until verified; weights are not silently changed.',
        estimates='Best-case and realistic work-hour budgets use existing source reuse. They are planning estimates, not elapsed AI runtime or guaranteed calendar dates. Human validation effort is included; waiting for data/reviewer availability is excluded.',
        external_dependencies=['Remaining real videos','Confirmed mouse/group/trial protocol','Independent manual reference and reviewer availability','Clean Windows validation environment','Trusted signing certificate if signing is requested'],
        phases=[dict(id=pid,title=title,steps=[dict(id=i,title=t,points=p,best_hours=b,realistic_hours=r,depends_on=d,work=w,acceptance=a,status='pending',evidence=[]) for i,t,p,b,r,d,w,a in steps]) for pid,title,steps in phases])

def render(plan):
    steps=[s for phase in plan['phases'] for s in phase['steps']]
    assert sum(s['points'] for s in steps)==1000
    assert len({s['id'] for s in steps})==len(steps)
    earned=sum(s['points'] for s in steps if s['status']=='complete')
    best=sum(s['best_hours'] for s in steps);real=sum(s['realistic_hours'] for s in steps)
    remaining=[s for s in steps if s['status']!='complete']
    plan['progress']=dict(earned_points=earned,total_points=1000,global_percent=earned/10,
        accepted_steps=sum(s['status']=='complete' for s in steps),total_steps=len(steps),
        total_best_hours=best,total_realistic_hours=real,
        remaining_best_hours=sum(s['best_hours'] for s in remaining),remaining_realistic_hours=sum(s['realistic_hours'] for s in remaining),
        as_of=datetime.now(timezone(timedelta(hours=-7))).strftime('%Y-%m-%d %H:%M PDT'))
    PLAN.write_text(json.dumps(plan,indent=2),encoding='utf-8')
    lines=[plan['product'], '='*65, f"Global progress: {earned}/1000 points = {earned/10:.1f}%",plan['scoring'],plan['estimates'],
           f'Total work budgets: best case {best:g} h; realistic {real:g} h.',
           f"Remaining: best case {plan['progress']['remaining_best_hours']:g} h; realistic {plan['progress']['remaining_realistic_hours']:g} h.",'']
    h=['<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Mouse Tracker Tabajara detailed roadmap</title><style>body{font:16px Segoe UI,Arial;color:#143e59;background:#f3f7fa;margin:40px;max-width:1200px}h1{font-size:36px}table{width:100%;border-collapse:collapse;background:white}td,th{padding:12px;text-align:left;border-bottom:1px solid #d4e1e8;vertical-align:top;font-size:13px}summary{cursor:pointer}details p{max-width:75ch}.done{color:#247969}.pending{color:#6d7378}progress{width:100%;accent-color:#176a96}.score{font-size:32px}section{margin:35px 0}.meta{color:#5b7180}.hours{white-space:nowrap}</style>',
       '<h1>Mouse Tracker Tabajara native WPF roadmap</h1>',f'<p class="score">{earned/10:.1f}% <small>({earned}/1000 accepted points)</small></p><progress value="'+str(earned)+'" max="1000"></progress>',
       '<p>'+html.escape(plan['scoring'])+'</p><p class="meta">'+html.escape(plan['estimates'])+'</p>',
       f'<p>Total budget: {best:g} h best case / {real:g} h realistic. Remaining: {plan["progress"]["remaining_best_hours"]:g} / {plan["progress"]["remaining_realistic_hours"]:g} h.</p>']
    for phase in plan['phases']:
        ps=phase['steps'];pe=sum(s['points'] for s in ps if s['status']=='complete');pt=sum(s['points'] for s in ps)
        lines += [f"PHASE {phase['id']}: {phase['title']} ({pe}/{pt} points)",'-'*65]
        h.append('<section><h2>'+html.escape(phase['id']+' '+phase['title'])+f' — {pe}/{pt} points</h2><table><tr><th>Step and acceptance</th><th>Best / realistic hours</th><th>Points</th><th>Status</th></tr>')
        for s in ps:
            deps=', '.join(s['depends_on']) or 'None'
            lines += [f"{s['id']} {s['title']}",f"  Difficulty: {s.get('difficulty', 'Hard')}; best {s['best_hours']:g} h / realistic {s['realistic_hours']:g} h; {s['points']} points; {s['status']}; dependencies: {deps}",
                      '  Work: '+s['work'],'  Acceptance: '+s['acceptance'],'  Evidence: '+('; '.join(s['evidence']) or 'Not yet accepted.'),'']
            h.append('<tr><td><details><summary><strong>'+html.escape(s['id']+' '+s['title'])+'</strong></summary><p>'+html.escape(s['work'])+'</p><p><b>Acceptance:</b> '+html.escape(s['acceptance'])+'</p><p><b>Dependencies:</b> '+html.escape(deps)+'</p><p><b>Evidence:</b> '+html.escape('; '.join(s['evidence']) or 'Not yet accepted.')+'</p></details></td>'+f'<td class="hours">{s["best_hours"]:g} / {s["realistic_hours"]:g}</td><td>{s["points"]}</td><td class="'+('done' if s['status']=='complete' else 'pending')+'">'+html.escape(s['status'])+'</td></tr>')
        h.append('</table></section>')
    lines += ['FULL CONCLUSION: a validated, usable native Windows release, not just a port that builds.',
              f"BEST-CASE TIME REMAINING: {plan['progress']['remaining_best_hours']:g} work hours, contingent on the stated inputs.",
              f"REALISTIC TIME REMAINING: {plan['progress']['remaining_realistic_hours']:g} work hours, plus external waiting time."]
    h.append('<p>Full conclusion requires held-out validation and researcher sign-off. As of '+html.escape(plan['progress']['as_of'])+'.</p></html>')
    if plan.get('current_delivery_roadmap'):
        lines.insert(3, 'Current collaborator-release execution and revised calendar estimates: docs/DELIVERY-ROADMAP.txt (separate from deferred scientific validation).')
        h.insert(1, '<p><a href="DELIVERY-ROADMAP.txt">Current collaborator-release steps, difficulty and remaining hours</a></p>')
    (ROOT/'docs/ROADMAP.txt').write_text('\n'.join(lines),encoding='utf-8')
    (ROOT/'docs/ROADMAP.html').write_text(''.join(h),encoding='utf-8')
    print(json.dumps(plan['progress'],indent=2))

if __name__=='__main__':
    render(json.loads(PLAN.read_text(encoding='utf-8')) if PLAN.exists() else initial_plan())
