using System.Globalization;

namespace DarkColony.Engine.Scenario;

/// <summary>Structural condition tree from a mission <c>.tro</c> trigger.</summary>
public abstract record ScenarioTriggerCondition;

public enum ScenarioConditionComparison { Equal, NotEqual, Greater, GreaterOrEqual, Less, LessOrEqual }
public enum ScenarioConditionLogical { And, Or }

public sealed record ScenarioConditionComparisonNode(
    ScenarioConditionValue Left,
    ScenarioConditionComparison Operator,
    ScenarioConditionValue Right) : ScenarioTriggerCondition;

public sealed record ScenarioConditionLogicalNode(
    ScenarioTriggerCondition Left,
    ScenarioConditionLogical Operator,
    ScenarioTriggerCondition Right) : ScenarioTriggerCondition;

/// <summary>
/// A numeric literal or original condition variable/function. Function
/// identities such as <c>c</c>, <c>s(...)</c>, <c>b(...)</c>, and <c>m(...)</c>
/// deliberately remain unevaluated until their native state bindings are
/// recovered.
/// </summary>
public abstract record ScenarioConditionValue;
public sealed record ScenarioConditionNumber(int Value) : ScenarioConditionValue;
public sealed record ScenarioConditionVariable(string Name, IReadOnlyList<ScenarioConditionValue> Arguments) : ScenarioConditionValue;

/// <summary>
/// Parser for the comparison/boolean expression syntax embedded in trigger
/// headers. It creates no simulation state and does not claim a meaning for
/// the source variables.
/// </summary>
public static class ScenarioTriggerConditionParser
{
    public static bool TryParse(string text, out ScenarioTriggerCondition? condition)
    {
        try
        {
            condition = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            condition = null;
            return false;
        }
    }

    public static ScenarioTriggerCondition Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var reader = new Reader(text);
        var condition = reader.ParseOr();
        reader.RequireEnd();
        return condition;
    }

    private sealed class Reader(string text)
    {
        private int position;

        public ScenarioTriggerCondition ParseOr()
        {
            var left = ParseAnd();
            while (Consume("||")) left = new ScenarioConditionLogicalNode(left, ScenarioConditionLogical.Or, ParseAnd());
            return left;
        }

        private ScenarioTriggerCondition ParseAnd()
        {
            var left = ParsePrimary();
            while (Consume("&&")) left = new ScenarioConditionLogicalNode(left, ScenarioConditionLogical.And, ParsePrimary());
            return left;
        }

        private ScenarioTriggerCondition ParsePrimary()
        {
            SkipWhitespace();
            if (Consume("("))
            {
                var nested = ParseOr();
                Require(")");
                return nested;
            }
            var left = ParseValue();
            var comparison = ParseComparison();
            var right = ParseValue();
            return new ScenarioConditionComparisonNode(left, comparison, right);
        }

        private ScenarioConditionValue ParseValue()
        {
            SkipWhitespace();
            if (position >= text.Length) throw Error("expected a value");
            if (text[position] == '-' || char.IsAsciiDigit(text[position]))
            {
                var start = position++;
                while (position < text.Length && char.IsAsciiDigit(text[position])) position++;
                if (!int.TryParse(text[start..position], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) throw Error("invalid number");
                return new ScenarioConditionNumber(value);
            }
            if (!char.IsAsciiLetter(text[position]) && text[position] != '_') throw Error("expected variable name");
            var nameStart = position++;
            while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_')) position++;
            var name = text[nameStart..position];
            var arguments = new List<ScenarioConditionValue>();
            if (Consume("("))
            {
                if (!Consume(")"))
                {
                    do { arguments.Add(ParseValue()); } while (Consume(","));
                    Require(")");
                }
            }
            return new ScenarioConditionVariable(name, arguments);
        }

        private ScenarioConditionComparison ParseComparison()
        {
            if (Consume("==")) return ScenarioConditionComparison.Equal;
            if (Consume("!=")) return ScenarioConditionComparison.NotEqual;
            if (Consume(">=")) return ScenarioConditionComparison.GreaterOrEqual;
            if (Consume("<=")) return ScenarioConditionComparison.LessOrEqual;
            if (Consume(">")) return ScenarioConditionComparison.Greater;
            if (Consume("<")) return ScenarioConditionComparison.Less;
            throw Error("expected comparison operator");
        }

        public void RequireEnd()
        {
            SkipWhitespace();
            if (position != text.Length) throw Error("unexpected trailing input");
        }

        private void Require(string token)
        {
            if (!Consume(token)) throw Error($"expected '{token}'");
        }

        private bool Consume(string token)
        {
            SkipWhitespace();
            if (!text.AsSpan(position).StartsWith(token, StringComparison.Ordinal)) return false;
            position += token.Length;
            return true;
        }

        private void SkipWhitespace()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        }

        private FormatException Error(string reason) => new($"Invalid trigger condition at character {position}: {reason}.");
    }
}
