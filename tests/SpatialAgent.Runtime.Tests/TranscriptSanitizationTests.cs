using FluentAssertions;
using SpatialAgent.Services;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// TranscriptWriter path sanitization Layer 1 tests.
    /// Verifies that user-profile paths, GDB paths, and UNC paths
    /// are replaced with tokens in the serialized JSONL output.
    /// </summary>
    public class TranscriptSanitizationTests : IDisposable
    {
        private TranscriptWriter? _tw;
        private string? _filePath;

        private string WriteAndRead(Action<TranscriptWriter> act)
        {
            var dir = Path.Combine(Path.GetTempPath(), "san_test_" + Guid.NewGuid().ToString("N")[..8]);
            _tw = new TranscriptWriter();
            _tw.Initialize(dir);
            _filePath = _tw.FilePath;
            act(_tw);
            // Flush & close before reading to avoid file-lock
            _tw.Dispose();
            _tw = null;
            return File.ReadAllText(_filePath!);
        }

        public void Dispose()
        {
            _tw?.Dispose();
            if (_filePath != null && File.Exists(_filePath))
            {
                try { File.Delete(_filePath); } catch { }
            }
        }

        [Fact]
        public void Tool_args_with_user_path_are_sanitized()
        {
            var jsonl = WriteAndRead(tw =>
            {
                tw.WriteToolStart(1, 1, "analysis_Buffer", new()
                {
                    ["in_features"] = @"C:\Users\ExampleUser\data.gdb\roads",
                    ["out_feature_class"] = @"C:\Users\ExampleUser\output.gdb\buffer_result",
                    ["buffer_distance"] = "100 Meters"
                });
            });

            jsonl.Should().Contain("%USERPROFILE%");
            jsonl.Should().NotContain(@"C:\Users\ExampleUser");
            // Layer name and expression should survive
            jsonl.Should().Contain("buffer_result");
            jsonl.Should().Contain("100 Meters");
        }

        [Fact]
        public void Gdb_paths_are_replaced_with_token()
        {
            var jsonl = WriteAndRead(tw =>
            {
                tw.WriteToolStart(1, 1, "analysis_Clip", new()
                {
                    ["in_features"] = @"C:\Example\SampleProject.gdb\BASE_01",
                    ["clip_features"] = @"D:\data\boundary.gdb\范围"
                });
            });

            jsonl.Should().Contain("{GDB}");
            jsonl.Should().NotContain(@"C:\Example\SampleProject.gdb");
            // Layer names within GDB paths are preserved after the path is tokenized
            jsonl.Should().Contain("BASE_01");
        }

        [Fact]
        public void Benign_values_are_not_sanitized()
        {
            var jsonl = WriteAndRead(tw =>
            {
                tw.WriteToolStart(1, 1, "management_CalculateField", new()
                {
                    ["in_table"] = "BASE_01",
                    ["field"] = "用地代码",
                    ["expression"] = "!shape.geodesicArea!"
                });
            });

            jsonl.Should().Contain("BASE_01");
            jsonl.Should().Contain("用地代码");
            jsonl.Should().Contain("geodesicArea");
            // Layer names / field names MUST survive sanitization intact
            jsonl.Should().Contain("\"BASE_01\"");
            jsonl.Should().Contain("用地代码");
        }

        [Fact]
        public void Session_start_project_path_is_sanitized()
        {
            var jsonl = WriteAndRead(tw =>
            {
                tw.WriteSessionStart("s1", "test-model", "",
                    @"C:\Users\ExampleUser\Documents\ArcGIS\Projects\test1");
            });

            jsonl.Should().Contain("session_start");
            jsonl.Should().Contain("%USERPROFILE%");
        }

        [Fact]
        public void Multiline_context_sanitization_does_not_consume_adjacent_json_properties()
        {
            var jsonl = WriteAndRead(tw =>
            {
                tw.Write(new
                {
                    type = "context",
                    project = "Project: Demo\nHomeFolder: G:\\work\\demo\nDefaultGdb: G:\\work\\demo\\data.gdb",
                    map = "Map A",
                    layer_count = 12
                });
            });

            jsonl.Should().Contain("\"map\":\"Map A\"");
            jsonl.Should().Contain("\"layer_count\":12");
            jsonl.Should().NotContain(@"G:\work\demo");
        }

        [Fact]
        public void Host_shutdown_is_a_synchronously_durable_session_boundary()
        {
            var jsonl = WriteAndRead(tw =>
                tw.WriteHostShutdown("s1", "ArcGISProClosing", SessionStatus.Interrupted, true));

            jsonl.Should().Contain("\"type\":\"host_shutdown\"");
            jsonl.Should().Contain("\"session_status\":\"Interrupted\"");
            jsonl.Should().Contain("\"had_active_run\":true");
            jsonl.Should().Contain("\"type\":\"log_closed\"");
        }

        [Fact]
        public void Write_does_not_throw_on_serialization_failure()
        {
            var tw = new TranscriptWriter();
            tw.Initialize(Path.Combine(Path.GetTempPath(), "san_test_" + Guid.NewGuid().ToString("N")[..8]));
            _filePath = tw.FilePath;
            try
            {
                var act = () =>
                {
                    tw.Write(new { ok = true });
                    tw.Dispose(); // close before delete
                };
                act.Should().NotThrow();
            }
            finally
            {
                if (_filePath != null && File.Exists(_filePath))
                {
                    try { File.Delete(_filePath); } catch { }
                }
            }
        }
    }
}
