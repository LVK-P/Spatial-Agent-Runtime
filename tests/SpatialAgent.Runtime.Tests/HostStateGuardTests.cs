using FluentAssertions;
using NSubstitute;
using SpatialAgent.Core;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class HostStateGuardTests
    {
        [Fact]
        public async Task Pending_host_edits_make_mutation_revision_unreliable()
        {
            var project = Substitute.For<IProjectReader>();
            project.GetContextForAIAsync().Returns("project");
            var map = Substitute.For<IMapReader>();
            map.GetMapInfoAsync().Returns("map");
            map.GetAllLayerSummariesAsync().Returns("layers");
            map.GetSelectionAsync().Returns("selection");
            map.GetContentRevisionAsync()
                .Returns("unreliable:pending-edits");
            var skills = Substitute.For<ISkillProvider>();
            var manager = new ContextManager(project, map, skills);
            var expected = new DataRevisionSnapshot
            {
                Fingerprint = "planned",
                IsReliable = true,
                ContentRevisionRequired = true,
                ContentRevisionReliable = true
            };

            HostStateGuardResult result =
                await manager.ValidateAsync(expected);

            result.IsCurrent.Should().BeFalse();
            result.Decision.Should().Be(HostRevisionDecision.ProbeUnreliable);
            result.Current.ContentRevisionRequired.Should().BeTrue();
            result.Current.ContentRevisionReliable.Should().BeFalse();
            result.Current.ReliabilityReason.Should()
                .Be("unreliable:pending-edits");
            result.Reason.Should().Contain("unreliable:pending-edits");
        }

        [Fact]
        public void Self_committed_layer_content_diff_is_current_for_execution()
        {
            var expected = new DataRevisionSnapshot
            {
                Fingerprint = "a",
                ProjectFingerprint = "p",
                MapFingerprint = "m",
                LayerFingerprint = "l1",
                SelectionFingerprint = "s",
                ContentFingerprint = "c1",
                IsReliable = true
            };
            var current = new DataRevisionSnapshot
            {
                Fingerprint = "b",
                ProjectFingerprint = "p",
                MapFingerprint = "m",
                LayerFingerprint = "l2",
                SelectionFingerprint = "s",
                ContentFingerprint = "c2",
                IsReliable = true
            };
            var budget = new ExpectedMutationSet
            {
                Items =
                {
                    new ExpectedMutation
                    {
                        ResourceKey = "roads",
                        Effects = DataEffectKind.ModifySpatialReference
                    }
                }
            };

            var result = HostRevisionPolicy.Evaluate(
                expected,
                current,
                budget,
                planHasCommittedMutation: true);

            result.IsCurrent.Should().BeTrue();
            result.Decision.Should().Be(HostRevisionDecision.SelfCommitted);
            result.ChangedComponents.Should().BeEquivalentTo("layer", "content");
        }

        [Fact]
        public void Mutation_budget_cannot_explain_change_before_first_commit()
        {
            var expected = new DataRevisionSnapshot
            {
                Fingerprint = "a",
                ProjectFingerprint = "p",
                MapFingerprint = "m",
                LayerFingerprint = "l1",
                SelectionFingerprint = "s",
                ContentFingerprint = "c1",
                IsReliable = true
            };
            var current = new DataRevisionSnapshot
            {
                Fingerprint = "b",
                ProjectFingerprint = "p",
                MapFingerprint = "m",
                LayerFingerprint = "l2",
                SelectionFingerprint = "s",
                ContentFingerprint = "c2",
                IsReliable = true
            };
            var budget = new ExpectedMutationSet
            {
                Items =
                {
                    new ExpectedMutation
                    {
                        ResourceKey = "roads",
                        Effects = DataEffectKind.ModifySpatialReference
                    }
                }
            };

            var result = HostRevisionPolicy.Evaluate(
                expected,
                current,
                budget,
                planHasCommittedMutation: false);

            result.IsCurrent.Should().BeFalse();
            result.Decision.Should().Be(HostRevisionDecision.ExternalChange);
        }

        [Fact]
        public void Selection_change_is_external()
        {
            var expected = new DataRevisionSnapshot
            {
                Fingerprint = "a",
                ProjectFingerprint = "p",
                MapFingerprint = "m",
                LayerFingerprint = "l",
                SelectionFingerprint = "s1",
                ContentFingerprint = "c",
                IsReliable = true
            };
            var current = new DataRevisionSnapshot
            {
                Fingerprint = "b",
                ProjectFingerprint = "p",
                MapFingerprint = "m",
                LayerFingerprint = "l",
                SelectionFingerprint = "s2",
                ContentFingerprint = "c",
                IsReliable = true
            };

            var result = HostRevisionPolicy.Evaluate(
                expected,
                current,
                new ExpectedMutationSet
                {
                    Items =
                    {
                        new ExpectedMutation
                        {
                            ResourceKey = "roads",
                            Effects = DataEffectKind.CreateDataset
                        }
                    }
                },
                planHasCommittedMutation: true);

            result.IsCurrent.Should().BeFalse();
            result.Decision.Should().Be(HostRevisionDecision.ExternalChange);
        }
    }
}
