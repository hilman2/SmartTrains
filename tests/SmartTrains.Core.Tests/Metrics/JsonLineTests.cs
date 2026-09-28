using System.Globalization;
using System.Text.Json;
using System.Threading;
using SmartTrains.Core.Metrics;
using Xunit;

namespace SmartTrains.Core.Tests.Metrics
{
    public class JsonLineTests
    {
        [Fact]
        public void AStandardParserReadsBackWhatWasWritten()
        {
            // Names hold quotes, umlauts, and whatever else players type.
            string line = new JsonLine().Begin()
                .Add("name", "Güterzug \"Nord\" C:\\Bahn\n\t\u0001")
                .Add("none", (string)null)
                .Add("count", 12345678901L)
                .Add("flag", true)
                .Add("trains", new long[] { 3, 5 })
                .End();

            Assert.DoesNotContain("\n", line);
            using (JsonDocument doc = JsonDocument.Parse(line))
            {
                JsonElement root = doc.RootElement;
                Assert.Equal("Güterzug \"Nord\" C:\\Bahn\n\t\u0001", root.GetProperty("name").GetString());
                Assert.Equal(JsonValueKind.Null, root.GetProperty("none").ValueKind);
                Assert.Equal(12345678901L, root.GetProperty("count").GetInt64());
                Assert.True(root.GetProperty("flag").GetBoolean());
                Assert.Equal(5, root.GetProperty("trains")[1].GetInt64());
            }
        }

        [Fact]
        public void NumbersUseAPointWhateverTheLanguage()
        {
            // The game runs in the player's culture; German writes 1,5.
            CultureInfo before = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                string line = new JsonLine().Begin().Add("a", 1.5).Add("b", 2.123456).Add("c", -0.0001).End();
                using (JsonDocument doc = JsonDocument.Parse(line))
                {
                    Assert.Equal(1.5, doc.RootElement.GetProperty("a").GetDouble());
                    Assert.Equal(2.123, doc.RootElement.GetProperty("b").GetDouble());
                    Assert.Equal(0.0, doc.RootElement.GetProperty("c").GetDouble());
                }
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = before;
            }
        }

        [Fact]
        public void NotANumberBecomesNull()
        {
            string line = new JsonLine().Begin().Add("a", double.NaN).Add("b", double.PositiveInfinity).End();
            using (JsonDocument doc = JsonDocument.Parse(line))
            {
                Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("a").ValueKind);
                Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("b").ValueKind);
            }
        }

        [Fact]
        public void BeginDropsTheLastObject()
        {
            var json = new JsonLine();
            json.Begin().Add("a", 1L).End();
            Assert.Equal("{\"b\":2}", json.Begin().Add("b", 2L).End());
        }
    }
}
