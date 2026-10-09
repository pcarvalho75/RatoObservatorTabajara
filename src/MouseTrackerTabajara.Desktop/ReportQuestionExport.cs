using System.Globalization;
using System.Net;
using System.Text;

namespace MouseTrackerTabajara.Desktop;

public static class ReportQuestionExport
{
    private static string N(double? n)=>n?.ToString("R",CultureInfo.InvariantCulture)??"";
    private static string E(string s)=>WebUtility.HtmlEncode(s);
    private static string TextCell(string s)=>"\""+(s.Length>0&&"=+-@".Contains(s[0])&&!double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out _)?"'":"")+s.Replace("\"","\"\"")+"\"";
    private static string Csv(IEnumerable<string> cells,char delimiter=',')=>string.Join(delimiter,cells.Select(TextCell));
    public static Dictionary<string,string> Payloads(StudyReportDocument document)
    {
        if(document.Contexts.All(c=>c.Questions is null))return [];
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        var groups=new StringBuilder("context,phase,day,comparison_offset_after_release_seconds,group,endpoint,unit,effective_window_seconds,expected_animals,included_animals,held_animals,excluded_animals,expected_trials,supported_trials,excluded_trials,equal_animal_mean,individual_minimum,individual_maximum,contributors,support\n");
        var portuguese=new StringBuilder("context;phase;day;comparison_offset_after_release_seconds;group;endpoint;unit;effective_window_seconds;expected_animals;included_animals;held_animals;excluded_animals;expected_trials;supported_trials;excluded_trials;equal_animal_mean;individual_minimum;individual_maximum;contributors;support\n");
        var trials=new StringBuilder("context,phase,day,trial,animal,group,endpoint,unit,value,effective_window_seconds,excluded,support\n");
        var coords=new StringBuilder("context,trial,animal,recording,source_sha256,run_id,source_frame,source_seconds,after_release_seconds,after_comparison_seconds,fps,width,height,raw_x_px,raw_y_px,raw_status,inspected_x_px,inspected_y_px,inspected_status,excluded,scope\n");
        var dist=new StringBuilder("context,trial,source_frame,source_seconds,adjacent_observed_distance_pool_diameters,gap_or_start,scope\n");
        var area=new StringBuilder("context,trial,source_frame,source_seconds,site_presence,wall_presence,quadrant,excluded,scope\n");
        var times=new StringBuilder("context,trial,animal,group,region,observed_seconds,effective_window_seconds,excluded,support\n");
        for(var ci=0;ci<document.Contexts.Length;ci++)
        {
            var c=document.Contexts[ci];if(c.Questions is not {} q)continue;
            foreach(var g in q.Groups)
            {
                var row=new[]{c.Id,c.Phase.ToString(),c.Day.ToString(CultureInfo.InvariantCulture),N(c.OffsetAfterRelease),g.Group,g.Endpoint,g.Unit,N(g.WindowSeconds),g.ExpectedAnimals.ToString(CultureInfo.InvariantCulture),g.IncludedAnimals.ToString(CultureInfo.InvariantCulture),g.HeldAnimals.ToString(CultureInfo.InvariantCulture),g.ExcludedAnimals.ToString(CultureInfo.InvariantCulture),g.ExpectedTrials.ToString(CultureInfo.InvariantCulture),g.SupportedTrials.ToString(CultureInfo.InvariantCulture),g.ExcludedTrials.ToString(CultureInfo.InvariantCulture),N(g.Mean),N(g.Minimum),N(g.Maximum),string.Join(" | ",g.Contributors),string.Join(" | ",g.Support)};
                groups.AppendLine(Csv(row));var translated=(string[])row.Clone();foreach(var i in new[]{3,7,15,16,17})translated[i]=translated[i].Replace('.',',');portuguese.AppendLine(Csv(translated,';'));
            }
            foreach(var t in q.Trials)
            {trials.AppendLine(Csv([c.Id,c.Phase.ToString(),c.Day.ToString(CultureInfo.InvariantCulture),t.Trial,t.Animal,t.Group,t.Endpoint,t.Unit,N(t.Value),N(t.WindowSeconds),t.Excluded.ToString(),string.Join(" | ",t.Support)]));if(t.Endpoint.EndsWith(" time",StringComparison.Ordinal))times.AppendLine(Csv([c.Id,t.Trial,t.Animal,t.Group,t.Endpoint,N(t.Value),N(t.WindowSeconds),t.Excluded.ToString(),string.Join(" | ",t.Support)]));}
            foreach(var p in q.Coordinates)
            {
                coords.AppendLine(Csv([c.Id,p.Trial,p.Animal,p.VideoId,p.SourceHash,p.RunId,p.Frame.ToString(CultureInfo.InvariantCulture),N(p.SourceSeconds),N(p.AfterReleaseSeconds),N(p.AfterComparisonSeconds),N(p.Fps),p.Width.ToString(CultureInfo.InvariantCulture),p.Height.ToString(CultureInfo.InvariantCulture),p.RawX?.ToString(CultureInfo.InvariantCulture)??"",p.RawY?.ToString(CultureInfo.InvariantCulture)??"",p.RawStatus,p.X?.ToString(CultureInfo.InvariantCulture)??"",p.Y?.ToString(CultureInfo.InvariantCulture)??"",p.Status,p.Excluded.ToString(),p.Scope]));
                dist.AppendLine(Csv([c.Id,p.Trial,p.Frame.ToString(CultureInfo.InvariantCulture),N(p.SourceSeconds),N(p.AdjacentDistance),(!p.AdjacentDistance.HasValue).ToString(),p.Scope]));area.AppendLine(Csv([c.Id,p.Trial,p.Frame.ToString(CultureInfo.InvariantCulture),N(p.SourceSeconds),p.SitePresence?.ToString()??"",p.WallPresence?.ToString()??"",p.Quadrant??"",p.Excluded.ToString(),p.Scope]));
            }
            for(var ti=0;ti<q.Paths.Length;ti++)
            {var path=q.Paths[ti];var stem=$"context-{ci+1:000}-trial-{ti+1:000}";result.Add(stem+"_PATH.svg",PathSvg(path,c));result.Add(stem+"_PCOORD.svg",CoordinateSvg(q,path.Trial,c));var density=q.Densities.FirstOrDefault(d=>d.Trial==path.Trial);result.Add(stem+"_DENSI.svg",DensitySvg(path.Trial,density?.KnownFractions??[],density?.UnknownFraction,c,support:density?.Support??"Source density unavailable; prerequisites held."));}
            result.Add($"context-{ci+1:000}_GROUP_DENSI.svg",GroupDensitySvg(c,document.Contexts.SelectMany(x=>x.Groups.SelectMany(g=>g.Map)).DefaultIfEmpty(0).Max()));
            result.Add($"context-{ci+1:000}_GROUP_DIFFERENCE.svg",DifferenceSvg(c));
        }
        result.Add("GROUP_SCALARS.csv",groups.ToString());result.Add("GROUP_SCALARS_pt-BR.csv",portuguese.ToString());result.Add("TRIAL_SCALARS.csv",trials.ToString());result.Add("COORD.csv",coords.ToString());result.Add("DIST.csv",dist.ToString());result.Add("AREA_RESULT.csv",area.ToString());result.Add("AREA_TIME.csv",times.ToString());
        return result;
    }
    private static StringBuilder Svg(string title,int width,int height)=>new($"<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='{height}' viewBox='0 0 {width} {height}' role='img'><title>{E(title)}</title><rect width='100%' height='100%' fill='#f3f7fa'/><text x='20' y='26' font-family='sans-serif' font-size='16' fill='#14344b'>{E(title)}</text>");
    public static string PathSvg(QuestionPath path,ReportContext c)
    {
        var svg=Svg(c.Id+" / "+path.Trial+" PATH · source pixels",640,470);svg.Append("<text x='20' y='48' font-family='sans-serif' font-size='11'>Gap-preserving segments; corrected stored path; no unknown bridge.</text><rect x='40' y='65' width='560' height='340' fill='white' stroke='#cad7df'/>");
        var scale=Math.Min(560d/Math.Max(1,path.Width),340d/Math.Max(1,path.Height));
        var left=40+(560-path.Width*scale)/2;var top=65+(340-path.Height*scale)/2;
        foreach(var segment in path.Segments)
        {var points=string.Join(' ',segment.Vertices.Select(v=>N(left+v.X*scale)+","+N(top+v.Y*scale)));svg.Append("<polyline fill='none' stroke='#147e98' stroke-width='2' points='"+points+"'><title>Source frames "+string.Join(',',segment.Vertices.Select(v=>v.Frame))+"</title></polyline>");}
        svg.Append("<text x='20' y='435' font-family='sans-serif' font-size='11'>"+E(path.Support)+"</text></svg>");return svg.ToString();
    }
    public static string CoordinateSvg(ReportQuestionData q,string trial,ReportContext c)
    {
        var rows=q.Coordinates.Where(p=>p.Trial==trial).ToArray();var indexed=rows.ToDictionary(p=>p.Frame);var path=q.Paths.Single(p=>p.Trial==trial);var svg=Svg(c.Id+" / "+trial+" PCOORD · pixels versus seconds",640,430);
        var axisLabel=rows.All(p=>p.AfterReleaseSeconds.HasValue)?"Time after release; comparison offset "+N(c.OffsetAfterRelease)+" s":"Source time; release/comparison anchor held";
        svg.Append("<text x='20' y='50' font-family='sans-serif' font-size='12'>"+E(axisLabel)+"; blue=X, orange=Y. Unknown gaps break lines.</text>");
        var begin=rows.Select(p=>p.AfterReleaseSeconds??p.SourceSeconds).DefaultIfEmpty(c.OffsetAfterRelease).Min();var end=rows.Select(p=>p.AfterReleaseSeconds??p.SourceSeconds).DefaultIfEmpty(c.OffsetAfterRelease+c.Duration).Max();
        foreach(var segment in path.Segments)foreach(var axis in new[]{"X","Y"})
        {var points=string.Join(' ',segment.Vertices.Select(v=>{var p=indexed[v.Frame];var value=axis=="X"?p.X:p.Y;return N(40+((p.AfterReleaseSeconds??p.SourceSeconds)-begin)/Math.Max(.001,end-begin)*560)+","+N(350-(value??0)/(double)Math.Max(1,axis=="X"?path.Width:path.Height)*260);}));svg.Append("<polyline fill='none' stroke='"+(axis=="X"?"#147e98":"#d27524")+"' stroke-width='2' points='"+points+"'/>");}
        svg.Append("<text x='40' y='390' font-family='sans-serif'>"+N(begin)+" to "+N(end)+" seconds · dimensions "+path.Width+"×"+path.Height+"px</text></svg>");return svg.ToString();
    }
    public static string DensitySvg(string title,IReadOnlyList<double> cells,double? unknown,ReportContext c,double? common=null,string support="")
    {
        var max=common??cells.DefaultIfEmpty(0).Max();var svg=Svg(c.Id+" / "+title+" DENSI · known time fraction",360,485);
        for(var i=0;i<cells.Count;i++){var strength=max>0?Math.Clamp(cells[i]/max,0,1):0;svg.Append($"<rect x='{35+i%12*24}' y='{55+i/12*24}' width='24' height='24' fill='rgb({(int)(238-strength*215)},{(int)(247-strength*123)},200)'><title>Cell{i}; known fraction{N(cells[i])}; upper{N(Math.Min(1,cells[i]+(unknown??0)))}</title></rect>");}
        svg.Append("<text x='20' y='345' font-family='sans-serif' font-size='11'>Normalized pool-radii grid; "+E(c.Orientation)+" orientation.</text><text x='20' y='365' font-family='sans-serif' font-size='10'>Scale0–"+N(max)+"; unknown mass "+N(unknown)+"; not accuracy/CI.</text><text x='20' y='385' font-family='sans-serif' font-size='10'>"+E(c.Phase+" day "+c.Day+"; offset "+N(c.OffsetAfterRelease)+" s; planned window "+N(c.Duration)+" s")+"</text><foreignObject x='20' y='395' width='320' height='88'><div xmlns='http://www.w3.org/1999/xhtml' style='font:10px sans-serif'>"+E(support)+"</div></foreignObject></svg>");return svg.ToString();
    }
    private static string ContextScope(ReportContext c)
    {
        var expected=c.Animals.Where(a=>c.Trials.Any(t=>t.Animal==a.Id)).ToArray();
        return c.Phase+" day "+c.Day+"; offset "+N(c.OffsetAfterRelease)+" s; planned window "+N(c.Duration)+" s; "+c.Orientation+"; overall context map animals expected/included/held/excluded "+expected.Length+"/"+expected.Count(a=>a.MapIncluded&&!a.Excluded)+"/"+expected.Count(a=>!a.MapIncluded&&!a.Excluded)+"/"+expected.Count(a=>a.Excluded);
    }
    private static string CohortScope(ReportContext c,string group)
    {
        var rows=c.Animals.Where(a=>a.Group==group&&c.Trials.Any(t=>t.Animal==a.Id)).ToArray();
        return group+" map animals expected/included/held/excluded "+rows.Length+"/"+rows.Count(a=>a.MapIncluded&&!a.Excluded)+"/"+rows.Count(a=>!a.MapIncluded&&!a.Excluded)+"/"+rows.Count(a=>a.Excluded);
    }
    private static string ScopeSvg(string scope,int y,int width)=>"<desc>"+E(scope)+"</desc><foreignObject x='20' y='"+y+"' width='"+width+"' height='40'><div xmlns='http://www.w3.org/1999/xhtml' style='font:11px sans-serif'>"+E(scope)+"</div></foreignObject>";
    public static string GroupDensitySvg(ReportContext c,double common)
    {var svg=Svg(c.Id+" GROUP DENSI · equal-animal known time fractions",Math.Max(400,c.Groups.Length*365),630);svg.Append(ScopeSvg(ContextScope(c),580,Math.Max(360,c.Groups.Length*365-40)));for(var i=0;i<c.Groups.Length;i++){var g=c.Groups[i];svg.Append($"<g transform='translate({i*365},45)'>"+DensitySvg(g.Group+" N="+g.IncludedAnimals.Length,g.Map,g.UnknownFraction,c,common,support:CohortScope(c,g.Group)+"; equal-animal map; held/excluded animals supply no map weight.")+"</g>");}svg.Append("</svg>");return svg.ToString();}
    public static string DifferenceSvg(ReportContext c)
    {
        var svg=Svg(c.Id+" GROUP DIFFERENCE · conservative missing-position bounds",760,Math.Max(110,c.Differences.Length*370+140));svg.Append(ScopeSvg(ContextScope(c),Math.Max(60,c.Differences.Length*370+95),720));
        for(var d=0;d<c.Differences.Length;d++){var diff=c.Differences[d];svg.Append($"<text x='20' y='{50+d*370}' font-family='sans-serif'>{E(diff.GroupA)} minus {E(diff.GroupB)} · lower (left), upper (right); same signed scale−1..1</text>");svg.Append(ScopeSvg(CohortScope(c,diff.GroupA)+"; "+CohortScope(c,diff.GroupB),65+d*370,720));for(var i=0;i<diff.Cells.Length;i++)foreach(var upper in new[]{false,true}){var v=upper?diff.Cells[i].Upper:diff.Cells[i].Lower;var alpha=Math.Clamp(Math.Abs(v),0,1);var color=v<0?$"rgb({(int)(245-alpha*220)},{(int)(245-alpha*135)},210)":$"rgb(220,{(int)(245-alpha*130)},{(int)(245-alpha*215)})";svg.Append($"<rect x='{35+(upper?370:0)+i%12*24}' y='{105+d*370+i/12*24}' width='24' height='24' fill='{color}'><title>Cell{i}; {(upper?"upper":"lower")}={N(v)}; not confidence interval</title></rect>");}}
        return svg.Append("</svg>").ToString();
    }
    public static string Html(StudyReportDocument document)
    {
        var html=new StringBuilder("<section><h2>Group questions — ready-to-import tables and source-linked figures</h2><p>Equal animal weights: all supported planned repeats are averaged within a mouse before mice are averaged. Observed extrema describe heterogeneity, not significance. Distance is adjacent observed motion only; unknown gaps are never bridged. No p-values, confidence intervals or causal memory score.</p><p>Download <a href='GROUP_SCALARS.csv'>group CSV</a>, <a href='GROUP_SCALARS_pt-BR.csv'>semicolon/decimal-comma pt-BR CSV</a>, <a href='TRIAL_SCALARS.csv'>all trial values/support</a>, <a href='COORD.csv'>COORD</a>, <a href='DIST.csv'>DIST</a>, <a href='AREA_RESULT.csv'>AREA RESULT</a> and <a href='AREA_TIME.csv'>AREA TIME</a>.</p>");
        for(var ci=0;ci<document.Contexts.Length;ci++)
        {var c=document.Contexts[ci];if(c.Questions is not {} q){html.Append("<p>Historical report lacks this question projection; recompute frozen inputs with compatible code. No missing values inferred.</p>");continue;}html.Append("<h3>"+E(c.Id)+" · "+c.Phase+" day"+c.Day+" · "+E(c.Orientation)+"</h3><table><tr><th>Group / endpoint</th><th>Mean / observed min–max</th><th>Units / effective window</th><th>Animals expected/included/held/excluded</th><th>Trial supported/expected/excluded</th></tr>");foreach(var g in q.Groups)html.Append($"<tr><td>{E(g.Group)} / {E(g.Endpoint)}</td><td>{N(g.Mean)} / {N(g.Minimum)}–{N(g.Maximum)}</td><td>{E(g.Unit)} / {N(g.WindowSeconds)}s</td><td>{g.ExpectedAnimals}/{g.IncludedAnimals}/{g.HeldAnimals}/{g.ExcludedAnimals}</td><td>{g.SupportedTrials}/{g.ExpectedTrials}/{g.ExcludedTrials}<br>{E(string.Join(" | ",g.Support))}</td></tr>");html.Append("</table><p>Conditional descriptive differences: "+E(string.Join(" | ",q.Differences.Select(d=>d.GroupA+" minus "+d.GroupB+" "+d.Endpoint+"="+(d.Difference.HasValue?N(d.Difference):"held")+" "+d.Unit)))+"</p><div class='grid'>"+GroupDensitySvg(c,document.Contexts.SelectMany(x=>x.Groups.SelectMany(g=>g.Map)).DefaultIfEmpty(0).Max())+DifferenceSvg(c)+"</div>");
            for(var ti=0;ti<q.Paths.Length;ti++){var path=q.Paths[ti];var stem=$"context-{ci+1:000}-trial-{ti+1:000}";html.Append("<details><summary>"+E(path.Trial)+" — individual path, coordinates and density</summary><p>Source dimensions/window are explicit; paths break at unknown frames. Numeric region/distance support is in trial CSV. Independent inspection still required for biological accuracy.</p><div class='grid'>"+PathSvg(path,c)+CoordinateSvg(q,path.Trial,c)+DensitySvg(path.Trial,q.Densities.FirstOrDefault(d=>d.Trial==path.Trial)?.KnownFractions??[],q.Densities.FirstOrDefault(d=>d.Trial==path.Trial)?.UnknownFraction,c,support:q.Densities.FirstOrDefault(d=>d.Trial==path.Trial)?.Support??"Source density unavailable; prerequisites held.")+"</div><p><a href='"+stem+"_PATH.svg'>PATH SVG</a> · <a href='"+stem+"_PCOORD.svg'>PCOORD SVG</a> · <a href='"+stem+"_DENSI.svg'>DENSI SVG</a></p></details>");}
        }
        return html.Append("</section>").ToString();
    }
}
