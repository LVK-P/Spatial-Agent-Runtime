using FluentAssertions;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// Regression tests for the 2026-07-19 output-routing incident:
    /// feature-dataset routing pinned outputs to the dataset's spatial
    /// reference (14 reprojections silently lost) and rejected standalone
    /// tables (ERROR 000210 on Statistics). Outputs now live flat at the
    /// agentworkspace.gdb root.
    /// </summary>
    public class WorkspaceRoutingTests
    {
        [Fact]
        public void Bare_name_routes_to_gdb_root_not_a_feature_dataset()
        {
            var ws = new WorkspaceTool();
            ws.Configure(@"C:\proj");

            string resolved = ws.ResolveOutputPath("tmp_道路面_ydyhmc统计", "analysis_Statistics");

            resolved.Should().Be(@"C:\proj\agentworkspace.gdb\tmp_道路面_ydyhmc统计");
            resolved.Should().NotContain(@"\Stats\");     // 表进要素数据集 = ERROR 000210
        }

        [Fact]
        public void Reprojection_output_routes_to_root_without_srs_pinning()
        {
            var ws = new WorkspaceTool();
            ws.Configure(@"C:\proj");

            ws.ResolveOutputPath("项目红线_CGCS2000", "management_Project")
                .Should().Be(@"C:\proj\agentworkspace.gdb\项目红线_CGCS2000");
        }

        [Theory]
        [InlineData(@"C:\other\out.gdb\layer")]
        [InlineData(@"other.gdb\layer")]
        [InlineData("in_memory/tmp1")]
        [InlineData(@"folder\name")]
        public void Qualified_paths_pass_through_unchanged(string path)
        {
            var ws = new WorkspaceTool();
            ws.Configure(@"C:\proj");

            ws.ResolveOutputPath(path).Should().Be(path);
        }

        [Fact]
        public void Unconfigured_workspace_leaves_bare_name_unchanged()
        {
            var ws = new WorkspaceTool();   // Configure/Initialize 未调用

            ws.ResolveOutputPath("tmp_x").Should().Be("tmp_x");
        }

        [Fact]
        public void Configure_sets_both_gdb_paths()
        {
            var ws = new WorkspaceTool();
            ws.Configure(@"D:\测试项目");

            ws.BackupGdb.Should().Be(@"D:\测试项目\agentbackup.gdb");
            ws.WorkspaceGdb.Should().Be(@"D:\测试项目\agentworkspace.gdb");
        }
    }
}
