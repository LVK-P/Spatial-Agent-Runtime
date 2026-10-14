using FluentAssertions;
using SpatialAgent.Core;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class GoalContractArchitectureTests
    {
        private readonly DefaultGoalScopeResolver _goals = new();
        private readonly DefaultPlanCoverageValidator _coverage = new();

        [Fact]
        public void Explicit_continuation_executes_only_unsatisfied_targets()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    Layer("roads_raw", "Polyline"),
                    Layer("roads_projected", "Polyline")
                }
            };
            var first = _goals.Resolve(
                "calculate roads_projected length",
                null,
                domain);
            first.SatisfiedTargets.Add("roads_projected");

            var continued = _goals.Resolve(
                "continue calculating length for all line layers",
                first,
                domain);
            var result = _coverage.Validate(
                continued,
                MeasurePlan("roads_raw"),
                domain);

            continued.TurnRelation.Should().Be(GoalTurnRelation.Continue);
            continued.GoalId.Should().Be(first.GoalId);
            continued.RequiredCapabilities.Should().BeEquivalentTo(
                first.RequiredCapabilities);
            continued.SatisfiedTargets.Should().Equal(
                "roads_projected");
            result.SatisfiedTargets.Should().Equal("roads_projected");
            result.ExpectedTargets.Should().Equal("roads_raw");
            result.IsComplete.Should().BeTrue();
        }

        [Fact]
        public void Universal_quantifier_alone_starts_a_new_goal()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    Layer("old_boundary", "Polygon"),
                    Layer("roads", "Polyline")
                }
            };
            var previous = _goals.Resolve(
                "calculate old_boundary area",
                null,
                domain);

            var current = _goals.Resolve(
                "calculate all line layers length",
                previous,
                domain);

            current.TurnRelation.Should().Be(GoalTurnRelation.New);
            current.GoalId.Should().NotBe(previous.GoalId);
            // Default Expand: category quantifier materializes physical targets.
            // 中文：默认扩展：类别量词实现物理目标。
            current.DomainCategories.Should().Equal("line");
            current.ExplicitTargets.Should().Equal("roads");
            // Unspecified layer + geometry class ⇒ all physical members of class.
            // 中文：未指定层+几何类⇒所有物理成员。
            current.ScopeExpansionRule.Should().Be("by_category:all_physical");
            current.ScopeSufficiency.Should().Be(GoalScopeSufficiency.Sufficient);
            current.RequiredCapabilities.Should().Equal(
                GisCapabilityIds.MeasureLength);
        }

        [Fact]
        public void Category_expand_keeps_all_physical_members_when_layer_unspecified()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    Layer("highway", "Polyline"),
                    Layer("highway_4490", "Polyline"),
                    Layer("乡镇边界_420300", "Polygon"),
                    Layer("乡镇边界_420300_4490", "Polygon"),
                    Layer("道路外用地面", "Polygon")
                }
            };

            var scope = _goals.Resolve(
                "线图层计算地线长度，面图层计算椭球面积，以米级单位计算",
                null,
                domain);

            scope.DomainCategories.Should().BeEquivalentTo("line", "polygon");
            scope.ScopeExpansionRule.Should().Be("by_category:all_physical");
            // Unspecified layer name means ALL members of the class, including
            // both raw and projected copies when both are on the map.
            // 中文：未指定图层名称指该类的所有成员，包括原始和投影副本，当两者都出现在地图上时。
            scope.ExplicitTargets.Should().BeEquivalentTo(
                "highway",
                "highway_4490",
                "乡镇边界_420300",
                "乡镇边界_420300_4490",
                "道路外用地面");
        }

        [Fact]
        public void Typed_parameter_patch_refines_previous_goal_without_keywords()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    Layer("highway", "Polyline"),
                    Layer("boundary", "Polygon")
                }
            };
            var previous = _goals.Resolve(
                "线图层计算地线长度，面图层计算椭球面积",
                null,
                domain);
            previous.SatisfiedTargets.AddRange(previous.ExplicitTargets);

            var refined = _goals.Resolve(
                "地线长度用米级单位，保留二位小数，椭球面积用公顷，保留4位小数",
                previous,
                domain);

            refined.TurnRelation.Should().Be(GoalTurnRelation.Refine);
            refined.GoalId.Should().Be(previous.GoalId);
            refined.ExplicitTargets.Should().BeEquivalentTo(
                "highway",
                "boundary");
            refined.SatisfiedTargets.Should().BeEmpty(
                "a parameter patch changes the desired state and invalidates old evidence");
            refined.ParameterConstraints.Should().ContainEquivalentOf(
                new GoalParameterConstraint
                {
                    AppliesToCapability = GisCapabilityIds.MeasureLength,
                    Key = "unit",
                    Value = "METERS"
                },
                options => options.Excluding(item => item.SourceText));
            refined.ParameterConstraints.Should().ContainEquivalentOf(
                new GoalParameterConstraint
                {
                    AppliesToCapability = GisCapabilityIds.MeasureLength,
                    Key = "decimal_places",
                    Value = "2"
                },
                options => options.Excluding(item => item.SourceText));
            refined.ParameterConstraints.Should().ContainEquivalentOf(
                new GoalParameterConstraint
                {
                    AppliesToCapability = GisCapabilityIds.MeasureArea,
                    Key = "unit",
                    Value = "HECTARES"
                },
                options => options.Excluding(item => item.SourceText));
            refined.ParameterConstraints.Should().ContainEquivalentOf(
                new GoalParameterConstraint
                {
                    AppliesToCapability = GisCapabilityIds.MeasureArea,
                    Key = "decimal_places",
                    Value = "4"
                },
                options => options.Excluding(item => item.SourceText));
        }

        [Fact]
        public void Category_expand_collapses_only_when_user_prefers_lineage_representative()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    Layer("highway", "Polyline"),
                    Layer("highway_4490", "Polyline")
                }
            };

            var scope = _goals.Resolve(
                "线图层最新版本计算地线长度",
                null,
                domain);

            scope.DomainCategories.Should().Equal("line");
            scope.ScopeExpansionRule.Should().Be("by_category:representative");
            scope.ExplicitTargets.Should().Contain("highway_4490");
            scope.ExplicitTargets.Should().NotContain("highway");
        }

        [Fact]
        public void Cc_calculate_length_provides_measure_length_capability()
        {
            var catalog = new DefaultCapabilityCatalog();
            var provided = catalog.ResolveProvidedCapabilities(
                descriptor: null,
                new ToolCall { Name = "cc_calculate_length" },
                effect: null);
            provided.Should().Contain(GisCapabilityIds.MeasureLength);
        }

        [Fact]
        public void Crs_align_to_reference_expands_all_other_layers()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    Layer("道路外用地面", "Polygon"),
                    Layer("村位置_点数据_420300", "Point"),
                    Layer("highway", "Polyline"),
                    Layer("乡镇边界_420300", "Polygon")
                }
            };

            var scope = _goals.Resolve(
                "对齐坐标系，统一采用道路外用地面图层的坐标系",
                null,
                domain);

            scope.ReferenceTargets.Should().Equal("道路外用地面");
            scope.RequiredCapabilities.Should().Contain(GisCapabilityIds.TransformCrs);
            scope.ScopeExpansionRule.Should().Be("except_reference");
            scope.ScopeSufficiency.Should().Be(GoalScopeSufficiency.Sufficient);
            scope.ExplicitTargets.Should().BeEquivalentTo(
                "村位置_点数据_420300",
                "highway",
                "乡镇边界_420300");
            scope.ExplicitTargets.Should().NotContain("道路外用地面");

            var plan = new AgentPlan
            {
                Steps =
                {
                    WriteStep("management_Project", "村位置_点数据_420300"),
                    WriteStep("management_Project", "highway"),
                    WriteStep("management_Project", "乡镇边界_420300")
                }
            };
            var coverage = _coverage.Validate(scope, plan, domain);
            coverage.Applicability.Should().Be(CoverageApplicability.Applicable);
            coverage.IsComplete.Should().BeTrue();
            coverage.UnexpectedTargets.Should().BeEmpty();
        }

        private static PlanStep WriteStep(string tool, string layer) =>
            new()
            {
                Id = 1,
                ToolCall = new ToolCall
                {
                    Name = tool,
                    Arguments = new()
                    {
                        ["in_dataset"] = layer,
                        ["out_dataset"] = layer
                    }
                },
                WriteSet = new() { layer },
                Effect = new InvocationEffect
                {
                    Profile = new EffectProfile
                    {
                        Effects = DataEffectKind.ModifySpatialReference
                            | DataEffectKind.CreateDataset
                    },
                    Target = new ApprovalTarget
                    {
                        Dataset = layer,
                        OutputTarget = layer
                    }
                }
            };

        [Fact]
        public void Township_density_contract_uses_semantics_without_prior_goal_pollution()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    Layer("乡镇边界", "Polygon"),
                    Layer("村位置", "Point"),
                    Layer("项目红线", "Polygon")
                }
            };
            var previous = _goals.Resolve(
                "计算项目红线面积",
                null,
                domain);

            var current = _goals.Resolve(
                "分析每个镇的村庄密度，分5个等级，用红色由浅到深渲染",
                previous,
                domain);

            current.TurnRelation.Should().Be(GoalTurnRelation.New);
            current.Quantifier.Should().Be(GoalTargetQuantifier.Explicit);
            current.ExplicitTargets.Should().Equal("乡镇边界");
            current.ReferenceTargets.Should().Equal("村位置");
            current.ExplicitTargets.Should().NotContain("项目红线");
            current.RequiredCapabilities.Should().BeEquivalentTo(
                GisCapabilityIds.SpatialAggregateWithin,
                GisCapabilityIds.ComputeMetric,
                GisCapabilityIds.ClassifyNumeric,
                GisCapabilityIds.RenderMap);
            current.ProofObligations.Single(proof =>
                    proof.CapabilityId == GisCapabilityIds.ClassifyNumeric)
                .Expected["class_count"].Should().Be("5");
        }

        [Fact]
        public void Exact_village_layer_is_reference_for_density_aggregation()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    Layer("乡镇边界", "Polygon"),
                    Layer("村位置", "Point")
                }
            };

            var goal = _goals.Resolve(
                "计算乡镇边界内村位置的密度",
                null,
                domain);

            goal.ExplicitTargets.Should().Equal("乡镇边界");
            goal.ReferenceTargets.Should().Equal("村位置");
        }

        [Fact]
        public void Missing_user_resource_cannot_be_replaced_by_a_known_layer()
        {
            var domain = new DomainContext
            {
                Layers = new() { Layer("existing_surface", "Polygon") }
            };
            var scope = _goals.Resolve(
                "对missing_surface赋值代码",
                null,
                domain);
            var result = _coverage.Validate(
                scope,
                UpdatePlan("existing_surface"),
                domain);

            scope.UnresolvedTargets.Should().Equal("missing_surface");
            result.IsComplete.Should().BeFalse();
            result.UnexpectedTargets.Should().Equal("existing_surface");
        }

        [Fact]
        public void Unresolved_explicit_target_requires_clarification()
        {
            var scope = new ResolvedGoalScope
            {
                Quantifier = GoalTargetQuantifier.Explicit,
                UnresolvedTargets = new() { "missing_surface" }
            };

            DefaultScopeExpander.ExpandInPlace(scope, new DomainContext());

            scope.ScopeSufficiency.Should().Be(
                GoalScopeSufficiency.ClarificationRequired);
            scope.ScopeExpansionRule.Should().Be("explicit:unresolved");
            scope.ScopeBasis.Should().Be("scope-unresolved:explicit");
        }

        [Fact]
        public void Invalid_area_plan_fails_density_capability_and_target_closure()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    Layer("乡镇边界", "Polygon"),
                    Layer("村位置", "Point"),
                    Layer("旧项目红线", "Polygon")
                }
            };
            var goal = _goals.Resolve(
                "分析每个镇的村庄密度，分5个等级，用红色渲染",
                null,
                domain);
            var plan = UpdatePlan("旧项目红线");
            plan.Steps[0].ProvidedCapabilities.Add(
                GisCapabilityIds.MeasureArea);
            var capabilities =
                new DefaultCapabilityClosureValidator().Validate(goal, plan);
            var targets = _coverage.Validate(goal, plan, domain);

            capabilities.IsComplete.Should().BeFalse();
            capabilities.MissingCapabilities.Should().Contain(
                GisCapabilityIds.SpatialAggregateWithin);
            targets.IsComplete.Should().BeFalse();
            targets.UnexpectedTargets.Should().Equal("旧项目红线");
        }

        [Fact]
        public void Area_execution_cannot_claim_density_goal_completion()
        {
            var goal = new ResolvedGoalScope
            {
                RequiredCapabilities = new()
                {
                    GisCapabilityIds.SpatialAggregateWithin,
                    GisCapabilityIds.ComputeMetric
                },
                ProofObligations = new()
                {
                    Proof(
                        "aggregate",
                        GisCapabilityIds.SpatialAggregateWithin),
                    Proof(
                        "metric",
                        GisCapabilityIds.ComputeMetric)
                }
            };
            var report = CompletionReportBuilder.Build(
                "density",
                new[]
                {
                    new StepExecutionResult
                    {
                        StepId = 1,
                        ToolName = "calculate-area",
                        Status = StepResultStatus.Completed,
                        Effect = new InvocationEffect
                        {
                            Profile = new EffectProfile
                            {
                                Effects = DataEffectKind.UpdateRows
                            }
                        },
                        ProvidedCapabilities = new()
                        {
                            GisCapabilityIds.MeasureArea
                        }
                    }
                },
                goal,
                new TargetCoverageResult
                {
                    IsApplicable = true,
                    IsComplete = true
                });

            report.Outcome.Should().Be(GoalOutcomeStatus.Partial);
            report.MissingProofs.Should().BeEquivalentTo(
                "aggregate",
                "metric");
        }

        [Fact]
        public void Display_name_edit_rebinds_same_resource_and_evidence()
        {
            var before = Layer("roads_alias", "Polyline");
            before.MapMemberId = "map:1";
            before.DatasetId = "dataset:1";
            before.StateFingerprint = "state:a";
            before.StateFingerprintReliable = true;
            var settled = _goals.Resolve(
                "calculate roads_alias length",
                null,
                new DomainContext { Layers = new() { before } });
            settled.SatisfiedTargets.Add("roads_alias");

            var renamed = Layer("roads_display_name", "Polyline");
            renamed.MapMemberId = "map:1";
            renamed.DatasetId = "dataset:1";
            renamed.StateFingerprint = "state:a";
            renamed.StateFingerprintReliable = true;
            var current = _goals.Resolve(
                "continue calculating length for all line layers",
                settled,
                new DomainContext { Layers = new() { renamed } });

            current.SatisfiedTargets.Should().Equal(
                "roads_display_name");
        }

        [Fact]
        public void Manual_content_edit_invalidates_prior_completion_evidence()
        {
            var before = Layer("roads", "Polyline");
            before.MapMemberId = "map:1";
            before.StateFingerprint = "state:before";
            before.StateFingerprintReliable = true;
            var settled = _goals.Resolve(
                "calculate roads length",
                null,
                new DomainContext { Layers = new() { before } });
            settled.SatisfiedTargets.Add("roads");

            var edited = Layer("roads", "Polyline");
            edited.MapMemberId = "map:1";
            edited.StateFingerprint = "state:after";
            edited.StateFingerprintReliable = true;
            var current = _goals.Resolve(
                "continue calculating length for all line layers",
                settled,
                new DomainContext { Layers = new() { edited } });

            current.SatisfiedTargets.Should().BeEmpty();
        }

        [Fact]
        public void Unrelated_content_revision_does_not_invalidate_schema_only_evidence()
        {
            var previous = new ResolvedGoalScope
            {
                GoalId = "schema-goal",
                Quantifier = GoalTargetQuantifier.Explicit,
                ExplicitTargets = new() { "roads" },
                SatisfiedTargets = new() { "roads" },
                RequiredCapabilities = new()
                {
                    GisCapabilityIds.AlterFieldSchema
                },
                ResourceHandles = new()
                {
                    new GoalResourceHandle
                    {
                        DisplayName = "roads",
                        MapMemberId = "map:1",
                        DatasetId = "dataset:1",
                        SchemaFingerprint = "schema:unchanged",
                        StateFingerprint = "state:before",
                        StateFingerprintReliable = true
                    }
                }
            };
            var currentLayer = Layer("roads", "Polyline");
            currentLayer.MapMemberId = "map:1";
            currentLayer.DatasetId = "dataset:1";
            currentLayer.SchemaFingerprint = "schema:unchanged";
            currentLayer.StateFingerprint = "state:other-content-write";
            currentLayer.StateFingerprintReliable = true;

            ResolvedGoalScope continued = _goals.Resolve(
                "continue",
                previous,
                new DomainContext { Layers = new() { currentLayer } });

            continued.SatisfiedTargets.Should().Equal("roads");
        }

        [Fact]
        public void Removed_resource_remains_unresolved_in_explicit_continuation()
        {
            var roads = Layer("roads", "Polyline");
            roads.MapMemberId = "map:1";
            var settled = _goals.Resolve(
                "calculate roads length",
                null,
                new DomainContext { Layers = new() { roads } });

            var current = _goals.Resolve(
                "continue calculating length for all line layers",
                settled,
                new DomainContext());

            current.UnresolvedTargets.Should().Contain("roads");
        }

        [Fact]
        public void Read_probe_does_not_claim_unresolved_goal_completion()
        {
            var domain = new DomainContext
            {
                Layers = new() { Layer("source_surface", "Polygon") }
            };
            var scope = _goals.Resolve(
                "inspect missing_surface",
                null,
                domain);
            var plan = new AgentPlan
            {
                Steps = new()
                {
                    new PlanStep
                    {
                        Id = 1,
                        ReadSet = new() { "missing_surface" },
                        Effect = new InvocationEffect
                        {
                            Profile = new EffectProfile
                            {
                                Effects = DataEffectKind.ReadData
                            },
                            Target = new ApprovalTarget
                            {
                                Layer = "missing_surface"
                            }
                        }
                    }
                }
            };
            var coverage = _coverage.Validate(scope, plan, domain);
            var report = CompletionReportBuilder.Build(
                "inspect missing_surface",
                new[]
                {
                    new StepExecutionResult
                    {
                        StepId = 1,
                        Status = StepResultStatus.Completed,
                        Effect = plan.Steps[0].Effect
                    }
                },
                scope,
                coverage);

            coverage.IsApplicable.Should().BeFalse();
            report.Outcome.Should()
                .Be(GoalOutcomeStatus.ClarificationRequired);
        }

        [Fact]
        public void Target_field_semantics_win_over_source_field()
        {
            var args = new Dictionary<string, object?>
            {
                ["source_field"] = "name",
                ["field_name"] = "code"
            };

            var target = ApprovalProtocol.BuildTarget(args);

            target.Field.Should().Be("code");
            ParameterSemanticResolver.FirstValue(
                    args,
                    null,
                    ParameterSemanticKind.SourceField)
                .Should().Be("name");
        }

        [Fact]
        public void Composite_schema_goal_preserves_clause_pairing_and_artifact_bindings()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    LayerWithFields("highway_4490", "Polyline", "DXCD"),
                    LayerWithFields("county", "Polygon", "TQMJ"),
                    LayerWithFields("city", "Polygon", "TQMJ"),
                    LayerWithFields("town", "Polygon", "TQMJ"),
                    LayerWithFields("land", "Polygon", "TQMJ")
                },
                Artifacts = new()
                {
                    Artifact(
                        "highway_4490",
                        "DXCD",
                        GisCapabilityIds.MeasureLength),
                    Artifact(
                        "county",
                        "TQMJ",
                        GisCapabilityIds.MeasureArea),
                    Artifact(
                        "city",
                        "TQMJ",
                        GisCapabilityIds.MeasureArea),
                    Artifact(
                        "town",
                        "TQMJ",
                        GisCapabilityIds.MeasureArea),
                    Artifact(
                        "land",
                        "TQMJ",
                        GisCapabilityIds.MeasureArea)
                }
            };

            var scope = _goals.Resolve(
                "批量删除线图层的地线长度字段和面图层的椭球面积字段",
                null,
                domain);
            var coverage = _coverage.Validate(
                scope,
                SchemaPlan(
                    ("highway_4490", "DXCD"),
                    ("county", "TQMJ"),
                    ("city", "TQMJ"),
                    ("town", "TQMJ"),
                    ("land", "TQMJ")),
                domain);

            scope.Requirements.Should().HaveCount(2);
            scope.Requirements.Single(requirement =>
                    requirement.DomainCategory == "line")
                .BoundFields.Should().Equal("DXCD");
            scope.Requirements.Single(requirement =>
                    requirement.DomainCategory == "polygon")
                .BoundFields.Should().Equal("TQMJ");
            scope.RequiredCapabilities.Should().Equal(
                GisCapabilityIds.AlterFieldSchema);
            scope.RequiredCapabilities.Should().NotContain(
                GisCapabilityIds.MeasureLength);
            scope.RequiredCapabilities.Should().NotContain(
                GisCapabilityIds.MeasureArea);
            coverage.GoalTargets.Should().HaveCount(5);
            coverage.ExpectedTargets.Should().HaveCount(5);
            coverage.UnexpectedTargets.Should().BeEmpty();
            coverage.IsComplete.Should().BeTrue();
        }

        [Fact]
        public void Explicit_goal_denominator_is_not_contracted_by_prestate()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    LayerWithFields("line_a", "Polyline", "legacy"),
                    LayerWithFields("line_b", "Polyline")
                }
            };
            var scope = _goals.Resolve(
                "remove the legacy property from all line layers",
                null,
                domain);

            var coverage = _coverage.Validate(
                scope,
                SchemaPlan(("line_a", "legacy")),
                domain);

            coverage.GoalTargets.Should().BeEquivalentTo(
                "line_a",
                "line_b");
            coverage.AlreadySatisfiedTargets.Should().Equal("line_b");
            coverage.ExpectedTargets.Should().Equal("line_a");
            coverage.IsComplete.Should().BeTrue();
        }

        [Fact]
        public void Unbound_semantic_field_is_unresolved_instead_of_shrinking_scope()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    LayerWithFields("roads", "Polyline", "DXCD")
                }
            };
            var scope = _goals.Resolve(
                "删除线图层的地线长度字段",
                null,
                domain);
            var plan = SchemaPlan(("roads", "DXCD"));
            plan.ResolvedScope = scope;
            plan.Coverage = _coverage.Validate(scope, plan, domain);
            plan.Validation = new PlanValidationResult
            {
                IsValid = true
            };
            plan.CapabilityClosure =
                new DefaultCapabilityClosureValidator().Validate(
                    scope,
                    plan);

            var decision =
                new DefaultPlanAdmissibilityEvaluator().Evaluate(plan);

            scope.ExplicitTargets.Should().Equal("roads");
            scope.UnresolvedRequirements.Should().Equal("requirement-1");
            plan.Coverage.GoalTargets.Should().Equal("roads");
            plan.Coverage.IsComplete.Should().BeFalse();
            decision.Decision.Should().Be(
                PlanAdmissibilityDecision.Clarify);
        }

        [Fact]
        public void Verified_field_transition_updates_cross_turn_artifact_ledger()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    LayerWithFields(
                        "roads",
                        "Polyline",
                        "DXCD")
                }
            };
            var ledger = new DefaultDomainArtifactLedger();
            ledger.Apply(domain, new StepExecutionResult
            {
                StepId = 1,
                ToolName = "logical-length-provider",
                Status = StepResultStatus.Completed,
                ProvidedCapabilities = new()
                {
                    GisCapabilityIds.MeasureLength
                },
                Effect = new InvocationEffect
                {
                    Target = new ApprovalTarget
                    {
                        Layer = "roads",
                        Field = "DXCD"
                    },
                    DesiredTargetState =
                        TargetStateExpectation.Populated
                }
            });

            domain.Artifacts.Should().ContainSingle();
            domain.Artifacts[0].SemanticType.Should().Be(
                GisCapabilityIds.MeasureLength);

            ledger.Apply(domain, new StepExecutionResult
            {
                StepId = 2,
                ToolName = "logical-schema-provider",
                Status = StepResultStatus.Completed,
                ProvidedCapabilities = new()
                {
                    GisCapabilityIds.AlterFieldSchema
                },
                Effect = new InvocationEffect
                {
                    Target = new ApprovalTarget
                    {
                        Layer = "roads",
                        Field = "DXCD"
                    },
                    DesiredTargetState =
                        TargetStateExpectation.Absent
                }
            });

            domain.Artifacts.Should().BeEmpty();
        }

        [Fact]
        public void Goal_control_plane_closes_only_evidence_bearing_contracts()
        {
            var controller = new DefaultGoalControlPlane();
            var scope = new ResolvedGoalScope
            {
                ProofObligations = new()
                {
                    new GoalProofObligation
                    {
                        Id = "target-coverage",
                        Kind = GoalProofKind.TargetCoverage,
                        Required = true
                    }
                }
            };
            var coverage = new TargetCoverageResult
            {
                IsApplicable = true,
                Applicability = CoverageApplicability.Applicable,
                IsGoalComplete = true
            };

            controller.Decide(scope, coverage, completionIsComplete: true)
                .Should().Be(GoalControlState.Closed);
            controller.Decide(
                    new ResolvedGoalScope(),
                    coverage,
                    completionIsComplete: true)
                .Should().Be(GoalControlState.Open);
        }

        private static GoalProofObligation Proof(
            string id,
            string capability) => new()
            {
                Id = id,
                Kind = GoalProofKind.VerifiedPostcondition,
                CapabilityId = capability,
                RequiresValidation = true
            };

        private static LayerInfo Layer(string name, string geometry) => new()
        {
            Name = name,
            GeometryType = geometry
        };

        private static LayerInfo LayerWithFields(
            string name,
            string geometry,
            params string[] fields) => new()
        {
            Name = name,
            GeometryType = geometry,
            Fields = fields.ToList()
        };

        private static DomainArtifactRecord Artifact(
            string owner,
            string field,
            string semanticType) => new()
        {
            OwnerLayer = owner,
            FieldName = field,
            SemanticType = semanticType,
            ProducerCapability = semanticType
        };

        private static AgentPlan SchemaPlan(
            params (string Target, string Field)[] targets) => new()
        {
            Steps = targets.Select((item, index) => new PlanStep
            {
                Id = index + 1,
                WriteSet = new() { item.Target },
                ProvidedCapabilities = new()
                {
                    GisCapabilityIds.AlterFieldSchema
                },
                Effect = new InvocationEffect
                {
                    Profile = new EffectProfile
                    {
                        Effects = DataEffectKind.AlterSchema
                    },
                    Target = new ApprovalTarget
                    {
                        Layer = item.Target,
                        Field = item.Field
                    },
                    DesiredTargetState =
                        TargetStateExpectation.Absent
                }
            }).ToList()
        };

        private static AgentPlan MeasurePlan(string target) => new()
        {
            Steps = new()
            {
                new PlanStep
                {
                    Id = 1,
                    WriteSet = new() { target },
                    ProvidedCapabilities = new()
                    {
                        GisCapabilityIds.MeasureLength
                    },
                    Effect = new InvocationEffect
                    {
                        Profile = new EffectProfile
                        {
                            Effects = DataEffectKind.UpdateRows
                        },
                        Target = new ApprovalTarget { Layer = target }
                    }
                }
            }
        };

        private static AgentPlan UpdatePlan(params string[] targets) => new()
        {
            Steps = targets.Select((target, index) => new PlanStep
            {
                Id = index + 1,
                WriteSet = new() { target },
                Effect = new InvocationEffect
                {
                    Profile = new EffectProfile
                    {
                        Effects = DataEffectKind.UpdateRows
                    },
                    Target = new ApprovalTarget { Layer = target }
                }
            }).ToList()
        };
    }
}
