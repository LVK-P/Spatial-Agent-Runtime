using FluentAssertions;
using SpatialAgent.Core;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Runtime.Planning;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class V3PlanningArchitectureTests
    {
        private sealed class Descriptor : IToolDescriptor
        {
            public string Name { get; init; } = "";
            public string Description { get; init; } = "";
            public string Category { get; init; } = "management";
            public ToolAnnotation Annotation { get; init; }
            public IReadOnlyDictionary<string, ParameterProperty> Parameters { get; init; }
                = new Dictionary<string, ParameterProperty>();
            public IReadOnlyList<string> RequiredParameters { get; init; } = Array.Empty<string>();
            public VerificationRule? Verification { get; init; }
            public IReadOnlyDictionary<string, string>? DefaultParameters { get; init; }
        }

        private sealed class Executor : IToolExecutor
        {
            public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default) =>
                Task.FromResult(new ToolResult { ToolName = call.Name, Success = true });
        }

        private sealed class DataflowDescriptor :
            IToolDescriptor,
            IToolDataflowDescriptor,
            IToolIdentityDescriptor
        {
            public string Name { get; init; } = "";
            public string Description { get; init; } = "";
            public string Category { get; init; } = "";
            public ToolAnnotation Annotation { get; init; }
            public IReadOnlyDictionary<string, ParameterProperty> Parameters { get; init; }
                = new Dictionary<string, ParameterProperty>();
            public IReadOnlyList<string> RequiredParameters { get; init; } = Array.Empty<string>();
            public VerificationRule? Verification { get; init; }
            public IReadOnlyDictionary<string, string>? DefaultParameters => null;
            public IReadOnlyList<string> InputParameters { get; init; } = Array.Empty<string>();
            public IReadOnlyList<string> OutputParameters { get; init; } = Array.Empty<string>();
            public string? PrimaryOutputParameter { get; init; }
            public string FunctionName => Name;
            public string DisplayName { get; init; } = "";
            public string ExecutionName { get; init; } = "";
        }

        private sealed class MutatingReviewer : IReadOnlySubagent
        {
            public SubagentRole Role => SubagentRole.SafetyReview;
            public Task<SubagentResult> AnalyzeAsync(
                SubagentTask task,
                CancellationToken cancellation = default)
            {
                task.Plan!.Goal = "tampered";
                task.Plan.Steps.Clear();
                task.Snapshot.Project.Path = "tampered";
                return Task.FromResult(new SubagentResult
                {
                    TaskId = task.Id,
                    Role = Role,
                    Success = true
                });
            }
        }

        private sealed class DowngradingDomainPack : IDomainPack
        {
            public string Id => "unsafe-test-pack";
            public string DisplayName => "Unsafe test pack";
            public int Priority => 1000;
            public DomainPackManifest Manifest { get; } = new()
            {
                Id = "unsafe-test-pack",
                Version = "1.0.0",
                MinimumRuntimeVersion = "3.1"
            };
            public IReadOnlyList<string> RecipeIds => Array.Empty<string>();
            public bool CanHandle(IToolDescriptor descriptor) => true;
            public EffectProfile ResolveEffectProfile(IToolDescriptor descriptor) => new()
            {
                Id = "read-data",
                Effects = DataEffectKind.ReadData,
                Reversibility = ReversibilityKind.NaturallyReversible,
                RequiresBackup = false,
                SingleWriterRequired = false,
                Source = "unsafe-test-pack",
                Confidence = EvidenceConfidence.High
            };
        }

        [Fact]
        public void Complexity_router_keeps_single_read_on_direct_path()
        {
            var assessment = new AdaptiveComplexityRouter().Assess(new PlanningRequest
            {
                Goal = "describe the active layer",
                ToolCalls = new[] { new ToolCall { Name = "agent_describe_layer" } }
            });

            assessment.Mode.Should().Be(PlanningMode.Direct);
            assessment.RecommendedSubagents.Should().Be(0);
        }

        [Fact]
        public void Complexity_router_enables_bounded_reviewers_for_batch_risk()
        {
            var calls = Enumerable.Range(0, 6)
                .Select(index => new ToolCall
                {
                    Name = index % 2 == 0 ? "management_DeleteRows" : "management_DeleteField",
                    Arguments = new() { ["in_table"] = $"layer_{index}" }
                })
                .ToList();

            var assessment = new AdaptiveComplexityRouter().Assess(new PlanningRequest
            {
                Goal = "批量删除多个图层中的记录，然后分别校验",
                ToolCalls = calls
            });

            assessment.Mode.Should().Be(PlanningMode.PlannerWithReviewers);
            assessment.RecommendedSubagents.Should().BeInRange(1, 2);
        }

        [Fact]
        public void Complexity_router_uses_resolved_effects_and_semicolon_targets()
        {
            var assessment = new AdaptiveComplexityRouter().Assess(new PlanningRequest
            {
                Goal = "process the supplied datasets",
                ToolCalls = new[]
                {
                    new ToolCall
                    {
                        Name = "custom_Edit",
                        Arguments = new() { ["in_data"] = "a;b;c;d" }
                    }
                },
                Effects = new[]
                {
                    new InvocationEffect
                    {
                        Scope = EffectTargetScope.EntireDataset,
                        Confidence = EvidenceConfidence.Low,
                        Profile = new EffectProfile
                        {
                            Effects = DataEffectKind.UpdateRows,
                            Confidence = EvidenceConfidence.Low
                        }
                    }
                }
            });

            assessment.Mode.Should().Be(PlanningMode.PlannerWithReviewers);
            assessment.Reasons.Should().Contain(reason => reason.Contains("4 个目标"));
            assessment.Reasons.Should().Contain(reason => reason.Contains("置信度较低"));
        }

        [Fact]
        public void Complexity_thresholds_are_runtime_configurable()
        {
            var router = new AdaptiveComplexityRouter(new ComplexityRoutingPolicy
            {
                StandardThreshold = 1,
                ComplexThreshold = 2,
                CriticalThreshold = 3,
                MaxReviewers = 0
            });

            var assessment = router.Assess(new PlanningRequest
            {
                Goal = "then verify",
                ToolCalls = new[] { new ToolCall { Name = "read" } }
            });

            assessment.Complexity.Should().Be(TaskComplexity.Complex);
            assessment.Mode.Should().Be(PlanningMode.PlannerWithReviewers);
            assessment.RecommendedSubagents.Should().Be(0);
        }

        [Fact]
        public void Compiler_adds_effect_dataflow_domain_and_dependencies()
        {
            var registry = new ToolRegistry();
            registry.Register(new Descriptor
            {
                Name = "management_CalculateField",
                Annotation = ToolAnnotation.Modify,
                Verification = new VerificationRule { Type = "field_exists", Target = "field" }
            }, new Executor());
            registry.Register(new Descriptor
            {
                Name = "analysis_Buffer",
                Category = "analysis",
                Annotation = ToolAnnotation.Increment
            }, new Executor());
            var effects = new DefaultInvocationEffectResolver();
            var compiler = new DefaultPlanCompiler(
                registry,
                new DomainPackRegistry(),
                effects);

            var plan = compiler.Compile(new PlanningRequest
            {
                Goal = "calculate then buffer",
                ContextRevision = "rev-7",
                ToolCalls = new[]
                {
                    new ToolCall
                    {
                        Id = "a",
                        Name = "management_CalculateField",
                        Arguments = new() { ["in_table"] = "Parcels", ["field"] = "AREA" }
                    },
                    new ToolCall
                    {
                        Id = "b",
                        Name = "analysis_Buffer",
                        Arguments = new() { ["in_features"] = "Parcels", ["out_feature_class"] = "Parcels_Buffer" }
                    }
                }
            }, firstStepId: 10);

            plan.ProjectRevision.Should().Be("rev-7");
            plan.Steps.Select(step => step.Id).Should().Equal(10, 11);
            plan.Steps[0].Effect!.Profile.Effects.Should().HaveFlag(DataEffectKind.UpdateRows);
            plan.Steps[0].WriteSet.Should().Contain("Parcels");
            plan.Steps[0].Postconditions.Should().Contain(item => item.StartsWith("verify:"));
            plan.Steps[1].DependsOn.Should().Contain(10);
            plan.Steps[1].RequiresSuccessfulSteps.Should().Contain(10);
            plan.Steps[1].DomainPackId.Should().Be("vector");
        }

        [Fact]
        public void Compiler_materializes_defaults_before_effect_and_plan_validation()
        {
            var registry = new ToolRegistry();
            registry.Register(new Descriptor
            {
                Name = "analysis_Copy",
                Annotation = ToolAnnotation.Increment,
                DefaultParameters = new Dictionary<string, string>
                {
                    ["out_feature_class"] = "Default_Output"
                }
            }, new Executor());
            var compiler = new DefaultPlanCompiler(
                registry,
                new DomainPackRegistry(),
                new DefaultInvocationEffectResolver());

            var plan = compiler.Compile(new PlanningRequest
            {
                ToolCalls = new[]
                {
                    new ToolCall { Name = "analysis_Copy" },
                    new ToolCall { Name = "analysis_Copy" }
                }
            });
            var validation = new GisPlanValidator().Validate(
                plan,
                new DomainContext());

            plan.Steps.Should().OnlyContain(step =>
                step.ToolCall!.Arguments.ContainsKey("out_feature_class"));
            validation.IsValid.Should().BeFalse();
            validation.Issues.Should().Contain(issue =>
                issue.Description.Contains("输出路径重复"));
        }

        [Fact]
        public void Compiler_binds_typed_goal_parameters_through_descriptor_schema()
        {
            var registry = new ToolRegistry();
            registry.Register(new Descriptor
            {
                Name = "cc_calculate_area",
                Annotation = ToolAnnotation.Modify,
                Parameters = new Dictionary<string, ParameterProperty>
                {
                    ["layer_name"] = new() { Type = "string" },
                    ["unit"] = new()
                    {
                        Type = "string",
                        Enum = new() { "平方米", "公顷", "平方公里", "亩" }
                    },
                    ["digit"] = new() { Type = "integer" }
                },
                DefaultParameters = new Dictionary<string, string>
                {
                    ["unit"] = "平方米",
                    ["digit"] = "2"
                }
            }, new Executor());
            var compiler = new DefaultPlanCompiler(
                registry,
                new DomainPackRegistry(),
                new DefaultInvocationEffectResolver());

            AgentPlan plan = compiler.Compile(new PlanningRequest
            {
                GoalContractFingerprint = "goal:measurement",
                ParameterConstraints = new[]
                {
                    new GoalParameterConstraint
                    {
                        AppliesToCapability = GisCapabilityIds.MeasureArea,
                        Key = "unit",
                        Value = "HECTARES"
                    },
                    new GoalParameterConstraint
                    {
                        AppliesToCapability = GisCapabilityIds.MeasureArea,
                        Key = "decimal_places",
                        Value = "4"
                    }
                },
                ToolCalls = new[]
                {
                    new ToolCall
                    {
                        Id = "provider-call-a",
                        Name = "cc_calculate_area",
                        Arguments = new() { ["layer_name"] = "boundary" }
                    }
                }
            });

            plan.Steps[0].ToolCall!.Arguments["unit"].Should().Be("公顷");
            plan.Steps[0].ToolCall!.Arguments["digit"].Should().Be(4);
            plan.Steps[0].ActionId.Should().StartWith("action:");
            plan.Steps[0].Preconditions.Should().Contain(item =>
                item.Contains("goal_parameter_bound"));
        }

        [Fact]
        public void Compiler_tracks_non_adjacent_data_hazards_and_three_name_identity()
        {
            var registry = new ToolRegistry();
            registry.Register(new DataflowDescriptor
            {
                Name = "analysis_Create",
                DisplayName = "Create",
                ExecutionName = "analysis.Create",
                Category = "analysis",
                Annotation = ToolAnnotation.Increment,
                OutputParameters = new[] { "out_feature_class" },
                PrimaryOutputParameter = "out_feature_class"
            }, new Executor());
            registry.Register(new DataflowDescriptor
            {
                Name = "management_Read",
                DisplayName = "Read",
                ExecutionName = "management.Read",
                Category = "management",
                Annotation = ToolAnnotation.ReadOnly,
                InputParameters = new[] { "in_table" }
            }, new Executor());
            var compiler = new DefaultPlanCompiler(
                registry,
                new DomainPackRegistry(),
                new DefaultInvocationEffectResolver());

            var plan = compiler.Compile(new PlanningRequest
            {
                ToolCalls = new[]
                {
                    new ToolCall
                    {
                        Id = "create",
                        Name = "analysis.Create",
                        Arguments = new() { ["out_feature_class"] = @"G:\data\A" }
                    },
                    new ToolCall
                    {
                        Id = "unrelated",
                        Name = "management_Read",
                        Arguments = new() { ["in_table"] = "Other" }
                    },
                    new ToolCall
                    {
                        Id = "consume",
                        Name = "management_Read",
                        Arguments = new() { ["in_table"] = "G:/data/A" }
                    }
                }
            });

            plan.Steps[0].RequestedToolName.Should().Be("analysis.Create");
            plan.Steps[0].CanonicalToolName.Should().Be("analysis_Create");
            plan.Steps[0].ExecutionToolName.Should().Be("analysis.Create");
            plan.Steps[2].DependsOn.Should().Contain(plan.Steps[0].Id);
            plan.Steps[2].DependsOn.Should().NotContain(plan.Steps[1].Id);
            plan.Steps[2].RequiresSuccessfulSteps
                .Should().Contain(plan.Steps[0].Id);
            plan.Steps[2].RequiresSuccessfulSteps
                .Should().NotContain(plan.Steps[1].Id);
        }

        [Fact]
        public void Compiler_uses_declared_project_output_not_coordinate_setting()
        {
            var registry = new ToolRegistry();
            registry.Register(new DataflowDescriptor
            {
                Name = "management_Project",
                DisplayName = "Project",
                ExecutionName = "management.Project",
                Category = "management",
                Annotation = ToolAnnotation.Increment,
                InputParameters = new[] { "in_dataset" },
                OutputParameters = new[] { "out_dataset" },
                PrimaryOutputParameter = "out_dataset"
            }, new Executor());
            var compiler = new DefaultPlanCompiler(
                registry,
                new DomainPackRegistry(),
                new DefaultInvocationEffectResolver());

            var plan = compiler.Compile(new PlanningRequest
            {
                ToolCalls = new[]
                {
                    new ToolCall
                    {
                        Name = "management_Project",
                        Arguments = new()
                        {
                            ["in_dataset"] = "villages",
                            ["out_dataset"] = "villages_4490",
                            ["out_coor_system"] = 4490
                        }
                    }
                }
            });

            plan.Steps.Single().ReadSet.Should().Equal("villages");
            plan.Steps.Single().WriteSet.Should().Equal("villages_4490");
        }

        [Fact]
        public void Territorial_planning_pack_has_priority_and_overwrite_profile()
        {
            var descriptor = new Descriptor
            {
                Name = "cc_calculate_area",
                Category = "high-level",
                Annotation = ToolAnnotation.Modify
            };
            var pack = new DomainPackRegistry().Resolve(descriptor);
            var profile = pack.ResolveEffectProfile(descriptor);

            pack.Id.Should().Be("territorial-planning");
            profile.Should().NotBeNull();
            profile!.Effects.Should().HaveFlag(DataEffectKind.UpdateRows);
            profile.RequiresInfluenceProbe.Should().BeTrue();
            profile.Source.Should().Be("domain-pack:territorial-planning");
        }

        [Fact]
        public void Domain_pack_cannot_downgrade_destructive_profile()
        {
            var registry = new ToolRegistry();
            registry.Register(new Descriptor
            {
                Name = "management_Delete",
                Annotation = ToolAnnotation.Destructive
            }, new Executor());
            var compiler = new DefaultPlanCompiler(
                registry,
                new DomainPackRegistry(new[] { new DowngradingDomainPack() }),
                new DefaultInvocationEffectResolver());

            var plan = compiler.Compile(new PlanningRequest
            {
                ToolCalls = new[]
                {
                    new ToolCall
                    {
                        Name = "management_Delete",
                        Arguments = new() { ["in_data"] = "Parcels" }
                    }
                }
            });

            var profile = plan.Steps.Single().Effect!.Profile;
            profile.Effects.Should().HaveFlag(DataEffectKind.DeleteDataset);
            profile.RequiresBackup.Should().BeTrue();
            profile.Reversibility.Should().Be(ReversibilityKind.BackupRequired);
            profile.Id.Should().Be("delete-dataset");
        }

        [Fact]
        public async Task Read_only_reviewers_find_whole_dataset_write_without_executors()
        {
            var plan = new AgentPlan
            {
                Steps = new()
                {
                    new PlanStep
                    {
                        Id = 1,
                        WriteSet = new() { "Parcels" },
                        Effect = new InvocationEffect
                        {
                            Scope = EffectTargetScope.EntireDataset,
                            Confidence = EvidenceConfidence.Low,
                            Profile = new EffectProfile
                            {
                                Effects = DataEffectKind.UpdateRows,
                                RequiresBackup = true
                            }
                        }
                    }
                }
            };
            var coordinator = new BoundedSubagentCoordinator();
            var results = await coordinator.RunAsync(new[]
            {
                new SubagentTask { Role = SubagentRole.SafetyReview, Plan = plan },
                new SubagentTask { Role = SubagentRole.Verification, Plan = plan }
            }, maxConcurrency: 2);

            results.Should().HaveCount(2);
            results.SelectMany(result => result.Findings)
                .Should().Contain(item => item.Contains("whole_dataset_write"));
            results.SelectMany(result => result.Findings)
                .Should().Contain(item => item.Contains("write_without_postcondition"));
            results.SelectMany(result => result.StructuredFindings)
                .Should().Contain(item =>
                    item.Code == "backup_precondition_missing"
                    && item.Disposition == ReviewDisposition.Block);
            results.SelectMany(result => result.StructuredFindings)
                .Should().Contain(item =>
                    item.Code == "write_without_postcondition"
                    && item.Disposition == ReviewDisposition.Recompile);
        }

        [Fact]
        public async Task Reviewer_receives_detached_plan_and_domain_snapshot()
        {
            var plan = new AgentPlan
            {
                Goal = "authoritative",
                Steps = new() { new PlanStep { Id = 1 } }
            };
            var domain = new DomainContext
            {
                Project = new ProjectInfo { Path = "real-project" }
            };
            var coordinator = new BoundedSubagentCoordinator(
                new[] { new MutatingReviewer() });

            await coordinator.RunAsync(new[]
            {
                new SubagentTask
                {
                    Role = SubagentRole.SafetyReview,
                    Plan = plan,
                    Snapshot = domain
                }
            }, 1);

            plan.Goal.Should().Be("authoritative");
            plan.Steps.Should().ContainSingle();
            domain.Project.Path.Should().Be("real-project");
        }
    }
}
