// Manages Fiddler Classic AutoResponder configuration, ordered rules, and FARX persistence.
using System.Reflection;
using System.Runtime.CompilerServices;
using Fiddler;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed class AutoResponderController
{
    private static readonly PropertyInfo RuleEnabledProperty = GetRuleProperty("IsEnabled");
    private static readonly PropertyInfo RuleMatchProperty = GetRuleProperty("sMatch");
    private static readonly PropertyInfo RuleActionProperty = GetRuleProperty("sAction");
    private static readonly PropertyInfo RuleImportedResponseProperty = GetRuleProperty("HasImportedResponse");
    private static readonly MethodInfo PromoteRuleMethod = GetAutoResponderMethod("PromoteRule", typeof(ResponderRule));
    private static readonly MethodInfo DemoteRuleMethod = GetAutoResponderMethod("DemoteRule", typeof(ResponderRule));
    private static readonly MethodInfo ImportFarxMethod = typeof(AutoResponder).GetMethod(
        "ImportFARX",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(string) },
        modifiers: null)
        ?? throw new MissingMethodException(typeof(AutoResponder).FullName, "ImportFARX");

    private readonly Dictionary<ResponderRule, string> _ruleIds =
        new Dictionary<ResponderRule, string>(ReferenceEqualityComparer<ResponderRule>.Instance);

    /// <summary>
    /// Returns the live AutoResponder switches and current rule count.
    /// </summary>
    public AutoResponderStatusResponse GetStatus()
    {
        return FiddlerThread.Invoke(() =>
        {
            var responder = GetResponder();
            return new AutoResponderStatusResponse
            {
                IsEnabled = responder.IsEnabled,
                PermitFallthrough = responder.PermitFallthrough,
                AcceptAllConnects = responder.AcceptAllConnects,
                UseLatency = responder.UseLatency,
                IsRuleListDirty = responder.IsRuleListDirty,
                RuleCount = responder.Rules.Count
            };
        });
    }

    /// <summary>
    /// Applies only supplied AutoResponder switches and returns the resulting state.
    /// </summary>
    /// <param name="request">The optional switch values to apply.</param>
    public AutoResponderStatusResponse Configure(ConfigureAutoResponderRequest request)
    {
        if (!request.IsEnabled.HasValue
            && !request.PermitFallthrough.HasValue
            && !request.AcceptAllConnects.HasValue
            && !request.UseLatency.HasValue)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "At least one AutoResponder setting is required.");
        }

        return FiddlerThread.Invoke(() =>
        {
            var responder = GetResponder();
            if (request.IsEnabled.HasValue)
            {
                responder.IsEnabled = request.IsEnabled.Value;
            }

            if (request.PermitFallthrough.HasValue)
            {
                responder.PermitFallthrough = request.PermitFallthrough.Value;
            }

            if (request.AcceptAllConnects.HasValue)
            {
                responder.AcceptAllConnects = request.AcceptAllConnects.Value;
            }

            if (request.UseLatency.HasValue)
            {
                responder.UseLatency = request.UseLatency.Value;
            }

            return new AutoResponderStatusResponse
            {
                IsEnabled = responder.IsEnabled,
                PermitFallthrough = responder.PermitFallthrough,
                AcceptAllConnects = responder.AcceptAllConnects,
                UseLatency = responder.UseLatency,
                IsRuleListDirty = responder.IsRuleListDirty,
                RuleCount = responder.Rules.Count
            };
        });
    }

    /// <summary>
    /// Lists every AutoResponder rule in evaluation order with runtime-stable IDs.
    /// </summary>
    public ListAutoResponderRulesResponse ListRules()
    {
        return FiddlerThread.Invoke(() =>
        {
            var responder = GetResponder();
            SynchronizeRuleIds(responder.Rules);
            return new ListAutoResponderRulesResponse
            {
                Rules = responder.Rules.Select((rule, index) => ToDto(rule, index)).ToList()
            };
        });
    }

    /// <summary>
    /// Appends one enabled or disabled AutoResponder match/action rule.
    /// </summary>
    /// <param name="request">The exact match, action, and optional rule metadata.</param>
    public AutoResponderMutationResponse AddRule(AddAutoResponderRuleRequest request)
    {
        ValidateRuleText(request.Match, nameof(request.Match));
        ValidateRuleText(request.Action, nameof(request.Action));
        ValidateComment(request.Comment);
        ValidateLatency(request.LatencyMilliseconds);

        return FiddlerThread.Invoke(() =>
        {
            var responder = GetResponder();
            if (responder.Rules.Count >= ProtocolConstants.MaxAutoResponderRules)
            {
                throw new BridgeOperationException(
                    ErrorCodes.Conflict,
                    $"AutoResponder is limited to {ProtocolConstants.MaxAutoResponderRules} rules through this bridge.");
            }

            var rule = responder.AddRule(request.Match, request.Action, request.IsEnabled)
                ?? throw new BridgeOperationException(ErrorCodes.Internal, "Fiddler did not create the AutoResponder rule.");
            rule.sComment = request.Comment ?? string.Empty;
            rule.bDisableOnMatch = request.DisableOnMatch;
            rule.iLatency = request.LatencyMilliseconds;
            responder.IsRuleListDirty = true;
            SynchronizeRuleIds(responder.Rules);
            return new AutoResponderMutationResponse
            {
                AffectedCount = 1,
                Rule = ToDto(rule, responder.Rules.IndexOf(rule))
            };
        });
    }

    /// <summary>
    /// Updates only supplied fields on a rule identified by its runtime ID.
    /// </summary>
    /// <param name="request">The rule ID and optional replacement fields.</param>
    public AutoResponderMutationResponse UpdateRule(UpdateAutoResponderRuleRequest request)
    {
        ValidateRuleId(request.RuleId);
        if (request.Match != null)
        {
            ValidateRuleText(request.Match, nameof(request.Match));
        }

        if (request.Action != null)
        {
            ValidateRuleText(request.Action, nameof(request.Action));
        }

        if (request.SetComment)
        {
            ValidateComment(request.Comment);
        }

        if (request.LatencyMilliseconds.HasValue)
        {
            ValidateLatency(request.LatencyMilliseconds.Value);
        }

        if (request.Match == null
            && request.Action == null
            && !request.IsEnabled.HasValue
            && !request.SetComment
            && !request.DisableOnMatch.HasValue
            && !request.LatencyMilliseconds.HasValue)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "At least one AutoResponder rule field is required.");
        }

        return FiddlerThread.Invoke(() =>
        {
            var responder = GetResponder();
            var rule = FindRule(responder, request.RuleId);
            if (request.Match != null)
            {
                RuleMatchProperty.SetValue(rule, request.Match, index: null);
            }

            if (request.Action != null)
            {
                RuleActionProperty.SetValue(rule, request.Action, index: null);
            }

            if (request.IsEnabled.HasValue)
            {
                RuleEnabledProperty.SetValue(rule, request.IsEnabled.Value, index: null);
            }

            if (request.SetComment)
            {
                rule.sComment = request.Comment ?? string.Empty;
            }

            if (request.DisableOnMatch.HasValue)
            {
                rule.bDisableOnMatch = request.DisableOnMatch.Value;
            }

            if (request.LatencyMilliseconds.HasValue)
            {
                rule.iLatency = request.LatencyMilliseconds.Value;
            }

            responder.IsRuleListDirty = true;
            return new AutoResponderMutationResponse
            {
                AffectedCount = 1,
                Rule = ToDto(rule, responder.Rules.IndexOf(rule))
            };
        });
    }

    /// <summary>
    /// Moves one rule to a zero-based evaluation index.
    /// </summary>
    /// <param name="request">The rule ID and destination index.</param>
    public AutoResponderMutationResponse MoveRule(MoveAutoResponderRuleRequest request)
    {
        ValidateRuleId(request.RuleId);
        return FiddlerThread.Invoke(() =>
        {
            var responder = GetResponder();
            if (request.Index < 0 || request.Index >= responder.Rules.Count)
            {
                throw new BridgeOperationException(
                    ErrorCodes.InvalidRequest,
                    $"Rule index must be between 0 and {Math.Max(0, responder.Rules.Count - 1)}.");
            }

            var rule = FindRule(responder, request.RuleId);
            MoveRuleUsingFiddler(responder, rule, request.Index);
            responder.IsRuleListDirty = true;
            return new AutoResponderMutationResponse
            {
                AffectedCount = 1,
                Rule = ToDto(rule, responder.Rules.IndexOf(rule))
            };
        });
    }

    /// <summary>
    /// Removes one AutoResponder rule after explicit confirmation.
    /// </summary>
    /// <param name="request">The rule ID and destructive confirmation.</param>
    public AutoResponderMutationResponse RemoveRule(RemoveAutoResponderRuleRequest request)
    {
        RequireConfirmation(request.Confirm, "Removing an AutoResponder rule requires confirmation.");
        ValidateRuleId(request.RuleId);
        return FiddlerThread.Invoke(() =>
        {
            var responder = GetResponder();
            var rule = FindRule(responder, request.RuleId);
            if (!responder.RemoveRule(rule))
            {
                throw new BridgeOperationException(ErrorCodes.Internal, "Fiddler did not remove the AutoResponder rule.");
            }

            _ruleIds.Remove(rule);
            responder.IsRuleListDirty = true;
            return new AutoResponderMutationResponse { AffectedCount = 1 };
        });
    }

    /// <summary>
    /// Removes every AutoResponder rule after explicit confirmation.
    /// </summary>
    /// <param name="request">The destructive confirmation.</param>
    public AutoResponderMutationResponse ClearRules(ClearAutoResponderRulesRequest request)
    {
        RequireConfirmation(request.Confirm, "Clearing AutoResponder rules requires confirmation.");
        return FiddlerThread.Invoke(() =>
        {
            var responder = GetResponder();
            var count = responder.Rules.Count;
            responder.ClearRules();
            responder.IsRuleListDirty = true;
            _ruleIds.Clear();
            return new AutoResponderMutationResponse { AffectedCount = count };
        });
    }

    /// <summary>
    /// Saves the complete ordered rule list to an absolute FARX path.
    /// </summary>
    /// <param name="request">The path and explicit overwrite controls.</param>
    public AutoResponderRulesFileResponse SaveRules(SaveAutoResponderRulesRequest request)
    {
        var path = ValidatePath(request.Path, mustExist: false);
        if (File.Exists(path))
        {
            if (!request.Overwrite)
            {
                throw new BridgeOperationException(ErrorCodes.Conflict, $"AutoResponder file already exists: {path}");
            }

            RequireConfirmation(request.ConfirmOverwrite, "Replacing an AutoResponder file requires confirmation.");
        }

        return FiddlerThread.Invoke(() =>
        {
            var responder = GetResponder();
            var temporaryPath = Path.Combine(
                Path.GetDirectoryName(path)!,
                $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.farx");
            try
            {
                if (!responder.SaveRules(temporaryPath))
                {
                    throw new BridgeOperationException(ErrorCodes.Internal, "Fiddler did not save the AutoResponder rules.");
                }

                File.Copy(temporaryPath, path, overwrite: request.Overwrite);
                responder.IsRuleListDirty = false;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return new AutoResponderRulesFileResponse { Path = path, RuleCount = responder.Rules.Count };
        });
    }

    /// <summary>
    /// Imports a FARX file or replaces the current ordered rule list after confirmation.
    /// </summary>
    /// <param name="request">The source path and replacement controls.</param>
    public AutoResponderRulesFileResponse LoadRules(LoadAutoResponderRulesRequest request)
    {
        var path = ValidatePath(request.Path, mustExist: true);
        if (request.Replace)
        {
            RequireConfirmation(request.ConfirmReplace, "Replacing all AutoResponder rules requires confirmation.");
        }

        return FiddlerThread.Invoke(() =>
        {
            var responder = GetResponder();
            var originalDirty = responder.IsRuleListDirty;
            var rollbackPath = Path.Combine(
                Path.GetTempPath(),
                $"fiddler-classic-cli-autoresponder-rollback-{Guid.NewGuid():N}.farx");
            if (!responder.SaveRules(rollbackPath))
            {
                throw new BridgeOperationException(ErrorCodes.Internal, "Fiddler could not snapshot the current AutoResponder rules.");
            }

            try
            {
                if (request.Replace)
                {
                    responder.ClearRules();
                }

                var loaded = request.Replace
                    ? responder.LoadRules(path, false)
                    : (bool)(ImportFarxMethod.Invoke(responder, new object[] { path }) ?? false);
                if (!loaded)
                {
                    throw new BridgeOperationException(ErrorCodes.InvalidRequest, "Fiddler could not load the FARX file.");
                }

                if (responder.Rules.Count > ProtocolConstants.MaxAutoResponderRules)
                {
                    throw new BridgeOperationException(
                        ErrorCodes.Conflict,
                        $"The loaded rule list exceeds {ProtocolConstants.MaxAutoResponderRules} rules.");
                }
            }
            catch
            {
                responder.ClearRules();
                if (!responder.LoadRules(rollbackPath, false))
                {
                    FiddlerApplication.Log.LogString("[FiddlerClassicCLI] Failed to restore AutoResponder rules after a FARX load error.");
                }

                responder.IsRuleListDirty = originalDirty;
                SynchronizeRuleIds(responder.Rules);
                throw;
            }
            finally
            {
                if (File.Exists(rollbackPath))
                {
                    File.Delete(rollbackPath);
                }
            }

            responder.IsRuleListDirty = true;
            SynchronizeRuleIds(responder.Rules);
            return new AutoResponderRulesFileResponse { Path = path, RuleCount = responder.Rules.Count };
        });
    }

    private static AutoResponder GetResponder()
    {
        return FiddlerApplication.oAutoResponder
            ?? throw new BridgeOperationException(ErrorCodes.Unavailable, "Fiddler's AutoResponder is not available.");
    }

    private ResponderRule FindRule(AutoResponder responder, string ruleId)
    {
        SynchronizeRuleIds(responder.Rules);
        return _ruleIds.FirstOrDefault(pair => string.Equals(pair.Value, ruleId, StringComparison.Ordinal)).Key
            ?? throw new BridgeOperationException(ErrorCodes.NotFound, $"AutoResponder rule '{ruleId}' was not found.");
    }

    private void SynchronizeRuleIds(IReadOnlyCollection<ResponderRule> rules)
    {
        var current = new HashSet<ResponderRule>(rules, ReferenceEqualityComparer<ResponderRule>.Instance);
        foreach (var stale in _ruleIds.Keys.Where(rule => !current.Contains(rule)).ToArray())
        {
            _ruleIds.Remove(stale);
        }

        foreach (var rule in rules)
        {
            if (!_ruleIds.ContainsKey(rule))
            {
                _ruleIds.Add(rule, Guid.NewGuid().ToString("N"));
            }
        }
    }

    private AutoResponderRuleDto ToDto(ResponderRule rule, int index)
    {
        return new AutoResponderRuleDto
        {
            RuleId = _ruleIds[rule],
            Index = index,
            Match = rule.sMatch,
            Action = rule.sAction,
            IsEnabled = (bool)(RuleEnabledProperty.GetValue(rule, index: null) ?? false),
            Comment = string.IsNullOrEmpty(rule.sComment) ? null : rule.sComment,
            DisableOnMatch = rule.bDisableOnMatch,
            LatencyMilliseconds = rule.iLatency,
            HasImportedResponse = (bool)(RuleImportedResponseProperty.GetValue(rule, index: null) ?? false)
        };
    }

    private static PropertyInfo GetRuleProperty(string name)
    {
        return typeof(ResponderRule).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(ResponderRule).FullName, name);
    }

    /// <summary>
    /// Moves a rule one native priority step at a time so Fiddler retains its own grouping and UI bookkeeping.
    /// </summary>
    /// <param name="responder">The live Fiddler AutoResponder engine.</param>
    /// <param name="rule">The rule whose priority is changing.</param>
    /// <param name="targetIndex">The requested zero-based position in Fiddler's flattened rule order.</param>
    private static void MoveRuleUsingFiddler(AutoResponder responder, ResponderRule rule, int targetIndex)
    {
        var currentIndex = responder.Rules.IndexOf(rule);
        while (currentIndex != targetIndex)
        {
            var movingEarlier = currentIndex > targetIndex;
            var method = movingEarlier ? PromoteRuleMethod : DemoteRuleMethod;
            var moved = (bool)(method.Invoke(responder, new object[] { rule }) ?? false);
            var nextIndex = responder.Rules.IndexOf(rule);
            if (!moved
                || nextIndex < 0
                || (movingEarlier && nextIndex >= currentIndex)
                || (!movingEarlier && nextIndex <= currentIndex))
            {
                throw new BridgeOperationException(
                    ErrorCodes.Conflict,
                    $"Fiddler could not move the AutoResponder rule to index {targetIndex}.");
            }

            currentIndex = nextIndex;
        }
    }

    /// <summary>
    /// Resolves a non-public AutoResponder operation used by Fiddler's own rule editor.
    /// </summary>
    /// <param name="name">The runtime method name.</param>
    /// <param name="parameterTypes">The exact method parameter types.</param>
    private static MethodInfo GetAutoResponderMethod(string name, params Type[] parameterTypes)
    {
        return typeof(AutoResponder).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: parameterTypes,
            modifiers: null)
            ?? throw new MissingMethodException(typeof(AutoResponder).FullName, name);
    }

    private static void ValidateRuleId(string ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId) || ruleId.Length > 64)
        {
            throw new BridgeOperationException(ErrorCodes.InvalidRequest, "The AutoResponder rule ID is invalid.");
        }
    }

    private static void ValidateRuleText(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > ProtocolConstants.MaxAutoResponderTextLength
            || value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"AutoResponder {field} must be non-empty, single-line text no longer than {ProtocolConstants.MaxAutoResponderTextLength} characters.");
        }
    }

    private static void ValidateComment(string? comment)
    {
        if (comment != null
            && (comment.Length > ProtocolConstants.MaxAutoResponderTextLength
                || comment.IndexOfAny(new[] { '\r', '\n' }) >= 0))
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"AutoResponder comments must be single-line text no longer than {ProtocolConstants.MaxAutoResponderTextLength} characters.");
        }
    }

    private static void ValidateLatency(int latencyMilliseconds)
    {
        if (latencyMilliseconds < 0 || latencyMilliseconds > ProtocolConstants.MaxBreakpointHoldMilliseconds)
        {
            throw new BridgeOperationException(
                ErrorCodes.InvalidRequest,
                $"Rule latency must be between 0 and {ProtocolConstants.MaxBreakpointHoldMilliseconds} milliseconds.");
        }
    }

    private static string ValidatePath(string path, bool mustExist)
    {
        if (!AutoResponderPaths.TryValidate(path, mustExist, out var fullPath, out var errorCode, out var errorMessage))
        {
            throw new BridgeOperationException(errorCode, errorMessage);
        }

        return fullPath;
    }

    private static void RequireConfirmation(bool confirmed, string message)
    {
        if (!confirmed)
        {
            throw new BridgeOperationException(ErrorCodes.ConfirmationRequired, message);
        }
    }

    private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new ReferenceEqualityComparer<T>();

        public bool Equals(T? x, T? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(T obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
