using FluentAssertions;
using SpatialAgent.Core;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class ProgressiveAdmissibilityTests
    {
        private readonly DefaultPlanAdmissibilityEvaluator _evaluator = new();
        private readonly DefaultPlanCoverageValidator _coverage = new();

        [Fact]
        public void Read_only_probe_is_executable_with_open_goal_obligations()
        {
            var plan = BasePlan(ReadStep(1));
            plan.CapabilityClosure = new CapabilityClosureResult
            {
                RequiredCapabilities = new() { "gis.example.final" },
                MissingCapabilities = new() { "gis.example.final" }
            };
            plan.Coverage = new TargetCoverageResult
            {
                IsApplicable = false,
                Applicability = CoverageApplicability.NotInScope,
                MissingTargets = new() { "target_a" }
            };

            PlanAdmissibilityResult result = _evaluator.Evaluate(plan);

            result.Decision.Should().Be(PlanAdmissibilityDecision.Probe);
            result.CanExecute.Should().BeTrue();
            result.OutstandingCapabilities.Should().Contain(
                "gis.example.final");
        }

        [Fact]
        public void Contributing_target_subset_executes_with_residual_obligations()
        {
            var plan = BasePlan(WriteStep(1, "target_a", "gis.example.a"));
            plan.CapabilityClosure = new CapabilityClosureResult
            {
                RequiredCapabilities = new()
                {
                    "gis.example.a",
                    "gis.example.b"
                },
                ProvidedCapabilities = new() { "gis.example.a" },
                MissingCapabilities = new() { "gis.example.b" }
            };
            plan.Coverage = new TargetCoverageResult
            {
                IsApplicable = true,
                Applicability = CoverageApplicability.Applicable,
                GoalTargets = new() { "target_a", "target_b" },
                PlannedTargets = new() { "target_a" },
                MissingTargets = new() { "target_b" }
            };

            PlanAdmissibilityResult result = _evaluator.Evaluate(plan);

            result.Decision.Should().Be(
                PlanAdmissibilityDecision.ExecuteWithObligations);
            result.CanExecute.Should().BeTrue();
            result.MakesGoalProgress.Should().BeTrue();
            result.OutstandingTargets.Should().Equal("target_b");
            result.Issues.Should().OnlyContain(issue =>
                issue.Enforcement ==
                ConstraintEnforcement.AllowWithObligation);
        }

        [Fact]
        public void Non_contributing_mutation_replans_without_becoming_safety_rule()
        {
            var plan = BasePlan(WriteStep(
                1,
                "target_a",
                "gis.unrelated"));
            plan.CapabilityClosure = new CapabilityClosureResult
            {
                RequiredCapabilities = new() { "gis.example.required" },
                ProvidedCapabilities = new() { "gis.unrelated" },
                MissingCapabilities = new() { "gis.example.required" }
            };
            plan.Coverage = CompleteCoverage("target_a");

            PlanAdmissibilityResult result = _evaluator.Evaluate(plan);

            result.Decision.Should().Be(PlanAdmissibilityDecision.Replan);
            result.CanExecute.Should().BeFalse();
            result.Issues.Should().ContainSingle(issue =>
                issue.ConstraintClass == PlanConstraintClass.GoalProgress
                && issue.Enforcement == ConstraintEnforcement.Replan);
        }

        [Fact]
        public void Contract_external_write_remains_authorization_boundary()
        {
            var plan = BasePlan(WriteStep(
                1,
                "outside",
                "gis.example.required"));
            plan.CapabilityClosure = new CapabilityClosureResult
            {
                RequiredCapabilities = new() { "gis.example.required" },
                ProvidedCapabilities = new() { "gis.example.required" }
            };
            plan.Coverage = new TargetCoverageResult
            {
                IsApplicable = true,
                Applicability = CoverageApplicability.Applicable,
                GoalTargets = new() { "inside" },
                PlannedTargets = new() { "outside" },
                UnexpectedTargets = new() { "outside" },
                MissingTargets = new() { "inside" }
            };

            PlanAdmissibilityResult result = _evaluator.Evaluate(plan);

            result.Decision.Should().Be(PlanAdmissibilityDecision.Replan);
            result.Issues.Should().ContainSingle(issue =>
                issue.ConstraintClass ==
                PlanConstraintClass.AuthorizationBoundary);
        }

        [Fact]
        public void Target_subset_success_is_immediately_durable_progress()
        {
            var scope = new ResolvedGoalScope
            {
                ExplicitTargets = new() { "target_a", "target_b" }
            };
            var coverage = new TargetCoverageResult
            {
                IsApplicable = true,
                Applicability = CoverageApplicability.Applicable,
                GoalTargets = new() { "target_a", "target_b" },
                ExpectedTargets = new() { "target_a", "target_b" },
                PlannedTargets = new() { "target_a" },
                MissingTargets = new() { "target_b" }
            };
            PlanStep step = WriteStep(1, "target_a", "gis.example");
            var plan = BasePlan(step);
            var tracker = new DefaultGoalProgressTracker();

            GoalProgressUpdate update = tracker.Update(
                scope,
                coverage,
                plan,
                new[]
                {
                    new StepExecutionResult
                    {
                        StepId = 1,
                        Status = StepResultStatus.Completed
                    }
                });

            update.NewlySatisfiedTargets.Should().Equal("target_a");
            update.ResidualTargets.Should().Equal("target_b");
            update.IsGoalComplete.Should().BeFalse();
            scope.SatisfiedTargets.Should().Equal("target_a");
            coverage.ProgressEvaluated.Should().BeTrue();
        }

        [Fact]
        public void Declared_tool_capability_precedes_name_heuristics()
        {
            const string capability = "gis.domain.declared";
            var catalog = new DefaultCapabilityCatalog();
            var descriptor = new DeclaredCapabilityDescriptor(capability);

            IReadOnlyList<string> provided =
                catalog.ResolveProvidedCapabilities(
                    descriptor,
                    new ToolCall { Name = "opaque_tool_42" },
                    effect: null);

            provided.Should().Contain(capability);
        }

        [Fact]
        public void Repeated_rejection_escalates_then_stops_thrashing()
        {
            var validation = new PlanValidationResult
            {
                IsValid = false,
                Issues =
                {
                    new PlanIssue
                    {
                        Severity = IssueSeverity.Error,
                        ConstraintClass = PlanConstraintClass.GoalProgress,
                        Enforcement = ConstraintEnforcement.Replan,
                        ToolName = "progress",
                        Description = "same residual"
                    }
                }
            };
            var admissibility = new PlanAdmissibilityResult
            {
                Decision = PlanAdmissibilityDecision.Replan
            };
            var recovery = new PlanRecoveryController();

            recovery.Observe(admissibility, validation)
                .ShouldDiversify.Should().BeFalse();
            recovery.Observe(admissibility, validation)
                .ShouldDiversify.Should().BeTrue();
            recovery.Observe(admissibility, validation)
                .ShouldStopThrashing.Should().BeTrue();
        }

        [Fact]
        public void Typed_validator_replan_is_not_collapsed_into_hard_block()
        {
            var plan = BasePlan(WriteStep(
                1,
                "target_a",
                "gis.example.required"));
            plan.Validation!.Issues.Add(new PlanIssue
            {
                Severity = IssueSeverity.Error,
                ConstraintClass = PlanConstraintClass.GoalProgress,
                Enforcement = ConstraintEnforcement.Replan,
                ToolName = "generic-planner",
                Description = "strategy cannot advance the goal"
            });

            PlanAdmissibilityResult result = _evaluator.Evaluate(plan);

            result.Decision.Should().Be(PlanAdmissibilityDecision.Replan);
            result.CanExecute.Should().BeFalse();
        }

        [Fact]
        public void Existing_mutation_without_goal_scope_requires_clarification()
        {
            var scope = new ResolvedGoalScope
            {
                ScopeSufficiency = GoalScopeSufficiency.NotRequired
            };
            var domain = new DomainContext
            {
                Layers = { new LayerInfo { Name = "target_a" } }
            };
            AgentPlan plan = BasePlan(WriteStep(
                1,
                "target_a",
                "gis.example.required"));
            plan.ResolvedScope = scope;
            plan.Coverage = _coverage.Validate(scope, plan, domain);
            plan.CapabilityClosure = new CapabilityClosureResult
            {
                RequiredCapabilities = new() { "gis.example.required" },
                ProvidedCapabilities = new() { "gis.example.required" }
            };

            PlanAdmissibilityResult result = _evaluator.Evaluate(plan);

            plan.Coverage.Applicability.Should().Be(
                CoverageApplicability.ScopeInsufficient);
            result.Decision.Should().Be(
                PlanAdmissibilityDecision.Clarify);
            result.CanExecute.Should().BeFalse();
        }

        [Fact]
        public void New_output_only_plan_has_a_contained_authorization_boundary()
        {
            var scope = new ResolvedGoalScope
            {
                ScopeSufficiency = GoalScopeSufficiency.NotRequired
            };
            AgentPlan plan = BasePlan(new PlanStep
            {
                Id = 1,
                ToolCall = new ToolCall { Name = "create_output" },
                WriteSet = { "new_output" },
                Effect = new InvocationEffect
                {
                    Scope = EffectTargetScope.OutputOnly,
                    OutputExists = false,
                    Profile = new EffectProfile
                    {
                        Effects = DataEffectKind.CreateDataset
                    },
                    Target = new ApprovalTarget
                    {
                        OutputTarget = "new_output"
                    }
                }
            });
            plan.ResolvedScope = scope;
            plan.Coverage = _coverage.Validate(
                scope,
                plan,
                new DomainContext());
            plan.CapabilityClosure = new CapabilityClosureResult();

            PlanAdmissibilityResult result = _evaluator.Evaluate(plan);

            plan.Coverage.Applicability.Should().Be(
                CoverageApplicability.OutputOnly);
            result.Decision.Should().Be(
                PlanAdmissibilityDecision.Execute);
            result.CanExecute.Should().BeTrue();
        }

        [Fact]
        public void Existing_output_cannot_use_output_only_authorization()
        {
            var scope = new ResolvedGoalScope
            {
                ScopeSufficiency = GoalScopeSufficiency.NotRequired
            };
            AgentPlan plan = BasePlan(new PlanStep
            {
                Id = 1,
                ToolCall = new ToolCall { Name = "overwrite_output" },
                WriteSet = { "existing_output" },
                Effect = new InvocationEffect
                {
                    Scope = EffectTargetScope.OutputOnly,
                    OutputExists = true,
                    Profile = new EffectProfile
                    {
                        Effects = DataEffectKind.CreateDataset
                            | DataEffectKind.OverwriteOutput
                    },
                    Target = new ApprovalTarget
                    {
                        OutputTarget = "existing_output"
                    }
                }
            });
            plan.Coverage = _coverage.Validate(
                scope,
                plan,
                new DomainContext());
            plan.CapabilityClosure = new CapabilityClosureResult();

            PlanAdmissibilityResult result = _evaluator.Evaluate(plan);

            plan.Coverage.Applicability.Should().Be(
                CoverageApplicability.ScopeInsufficient);
            result.Decision.Should().Be(
                PlanAdmissibilityDecision.Clarify);
        }

        [Fact]
        public void Plausible_capability_alone_cannot_authorize_mutation()
        {
            var plan = BasePlan(WriteStep(
                1,
                "target_a",
                GisCapabilityIds.MeasureArea));
            plan.CapabilityClosure = new CapabilityClosureResult
            {
                RequiredCapabilities = new()
                {
                    GisCapabilityIds.MeasureLength
                },
                ProvidedCapabilities = new()
                {
                    GisCapabilityIds.MeasureArea
                },
                MissingCapabilities = new()
                {
                    GisCapabilityIds.MeasureLength
                },
                PlausibleButUnconfirmed = new()
                {
                    GisCapabilityIds.MeasureLength
                }
            };
            plan.Coverage = CompleteCoverage("target_a");

            PlanAdmissibilityResult result = _evaluator.Evaluate(plan);

            result.Decision.Should().Be(PlanAdmissibilityDecision.Replan);
            result.CanExecute.Should().BeFalse();
        }

        [Fact]
        public void Generic_calculate_prefix_does_not_cross_measure_capabilities()
        {
            DefaultCapabilityCatalog.PlausiblyProvidesCapability(
                    "cc_calculate_area",
                    descriptor: null,
                    GisCapabilityIds.MeasureLength)
                .Should().BeFalse();
        }

        private static AgentPlan BasePlan(params PlanStep[] steps) => new()
        {
            Validation = new PlanValidationResult { IsValid = true },
            Steps = steps.ToList()
        };

        private static PlanStep ReadStep(int id) => new()
        {
            Id = id,
            ToolCall = new ToolCall { Name = "read_probe" },
            Effect = new InvocationEffect
            {
                Profile = new EffectProfile
                {
                    Effects = DataEffectKind.ReadData
                }
            }
        };

        private static PlanStep WriteStep(
            int id,
            string target,
            string capability) => new()
            {
                Id = id,
                ToolCall = new ToolCall { Name = "write_tool" },
                WriteSet = new() { target },
                ProvidedCapabilities = new() { capability },
                Effect = new InvocationEffect
                {
                    Profile = new EffectProfile
                    {
                        Effects = DataEffectKind.UpdateRows
                    },
                    Target = new ApprovalTarget { Layer = target }
                }
            };

        private static TargetCoverageResult CompleteCoverage(string target) =>
            new()
            {
                IsApplicable = true,
                Applicability = CoverageApplicability.Applicable,
                IsComplete = true,
                GoalTargets = new() { target },
                ExpectedTargets = new() { target },
                PlannedTargets = new() { target }
            };

        private sealed class DeclaredCapabilityDescriptor :
            IToolDescriptor,
            IToolCapabilityDescriptor
        {
            public DeclaredCapabilityDescriptor(string capability)
            {
                CapabilityClaims = new[]
                {
                    new ToolCapabilityClaim
                    {
                        CapabilityId = capability
                    }
                };
            }

            public string Name => "opaque_tool_42";
            public string Description => "opaque";
            public string Category => "domain";
            public ToolAnnotation Annotation => ToolAnnotation.ReadOnly;
            public IReadOnlyDictionary<string, ParameterProperty> Parameters =>
                new Dictionary<string, ParameterProperty>();
            public IReadOnlyList<string> RequiredParameters =>
                Array.Empty<string>();
            public VerificationRule? Verification => null;
            public IReadOnlyDictionary<string, string>? DefaultParameters =>
                null;
            public IReadOnlyList<ToolCapabilityClaim> CapabilityClaims
            {
                get;
            }
        }
    }
}
