using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Inlay.Core.Emoji;
using Inlay.Core.Search;

BenchmarkSwitcher.FromAssembly(typeof(Inlay.Benchmarks.SearchBenchmarks).Assembly).Run(args);

namespace Inlay.Benchmarks
{

    /// <summary>One search over the full emoji set, per query shape (what each keystroke costs).</summary>
    [MemoryDiagnoser]
    public class SearchBenchmarks
    {
        private EmojiData _data = null!;
        private SearchEngine _engine = null!;
        private readonly SearchResult[] _results = new SearchResult[24];

        [Params("sm", "smi", "smile", "thumbsup", "hrt", "smiel", "thnumbs", "laughign", "zzzzqx")]
        public string Query { get; set; } = "";

        [GlobalSetup]
        public void Setup()
        {
            _data = EmojiData.LoadEmbedded();
            _engine = new SearchEngine(_data, new SnippetIndex([("website", "https://example.com/"), ("email", "someone@example.com")]));
        }

        [GlobalCleanup]
        public void Cleanup() => _data.Dispose();

        [Benchmark]
        public int Search() => _engine.Search(Query, _results);
    }

    /// <summary>Loading the embedded dataset and building the engine (startup / data-pack swap cost).</summary>
    [MemoryDiagnoser]
    public class LoadBenchmarks
    {
        [Benchmark]
        public int LoadAndBuildEngine()
        {
            using var data = EmojiData.LoadEmbedded();
            var engine = new SearchEngine(data, null);
            return engine.Emoji!.Count;
        }
    }
}
