using System.Text.Json;

static class Summary
{
    static double Median(IEnumerable<double> values)
    {
        var a=values.Order().ToArray();
        return a.Length%2==1 ? a[a.Length/2] : (a[a.Length/2-1]+a[a.Length/2])/2;
    }

    public static void Save(string root, IReadOnlyList<Sample> samples)
    {
        object Describe(IEnumerable<Sample> input)
        {
            var a=input.ToArray();
            return new { n=a.Length, medianMilliseconds=Median(a.Select(x=>x.Milliseconds)),
                minimumMilliseconds=a.Min(x=>x.Milliseconds),maximumMilliseconds=a.Max(x=>x.Milliseconds),
                medianCpuMilliseconds=Median(a.Select(x=>x.CpuMilliseconds)),
                medianReadBytes=Median(a.Select(x=>(double)x.ReadBytes)),
                medianWriteBytes=Median(a.Select(x=>(double)x.WriteBytes)) };
        }
        var groups=samples.GroupBy(x=>new{x.Scenario,x.Bytes})
            .Select(g=>new {g.Key.Scenario,g.Key.Bytes,
                before=Describe(g.Where(x=>x.Variant=="before")),
                current=Describe(g.Where(x=>x.Variant=="optimized"))}).ToArray();
        var pairs=samples.Where(x=>x.Scenario=="workflow-undo").GroupBy(x=>x.Round).Select(g=>{
            var b=g.Single(x=>x.Variant=="before");var a=g.Single(x=>x.Variant=="optimized");
            return new {round=g.Key,order=g.Key%2==0?"AB":"BA",
                deltaMilliseconds=a.Milliseconds-b.Milliseconds,
                deltaPercent=100*(a.Milliseconds/b.Milliseconds-1)};
        }).ToArray();
        object? paired=null;
        if(pairs.Length>=2)
        {
            var rng=new Random(18371);
            var bootstrap=Enumerable.Range(0,20000).Select(_=>Median(
                Enumerable.Range(0,pairs.Length).Select(_=>pairs[rng.Next(pairs.Length)].deltaPercent))).Order().ToArray();
            paired=new { medianDeltaPercent=Median(pairs.Select(x=>x.deltaPercent)),
                medianDeltaMilliseconds=Median(pairs.Select(x=>x.deltaMilliseconds)),
                bootstrap95Percent=new[]{bootstrap[499],bootstrap[19499]},samples=pairs };
        }
        File.WriteAllText(Path.Combine(root,"summary.json"),JsonSerializer.Serialize(
            new{groups,undoPairs=paired},new JsonSerializerOptions{WriteIndented=true}));
    }
}
