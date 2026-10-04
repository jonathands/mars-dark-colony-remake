namespace DarkColony.Engine.Missions;

/// <summary>Values a trigger expression can read (evaluator <c>0x43CF2C</c>).</summary>
public interface ITriggerExpressionContext
{
    /// <summary><c>c</c>: world counter <c>+0x52C</c> shifted right by four.</summary>
    int Clock { get; }
    /// <summary><c>r</c>: the next value of the shared random stream (<c>0x411DB4</c>).</summary>
    int NextRandom();
    /// <summary><c>t</c> / <c>S</c>: the tripping unit's entity type and team; null outside trip triggers.</summary>
    (int EntityType, int Team)? Unit { get; }
    /// <summary><c>b(p,s)</c>: health of player p's city slot s (player <c>+0xBD4 + s * 4</c>).</summary>
    int SlotHealth(int player, int slot);
    /// <summary><c>v(x,z,p)</c>: player p's visibility bit at a cell.</summary>
    bool CellVisible(int x, int z, int player);
    /// <summary><c>s(p,k)</c>: player statistic k (<c>0x4956E0</c>).</summary>
    int PlayerStat(int player, int stat);
    /// <summary><c>s(p,k,i)</c>: per-entity-type statistic k (<c>0x495860</c>).</summary>
    int TypeStat(int player, int stat, int entityType);
    /// <summary><c>m(x,z)</c>: the map's live-mine bit (load byte 3, bit 2).</summary>
    bool MineAlive(int x, int z);
    /// <summary><c>u(i)</c>: the low word of dword i of the script array at <c>0x4FE04C</c>.</summary>
    int ScriptWord(int index);
}

/// <summary>
/// The native trigger expression language: a single-character-name compiler
/// (<c>0x43C5B8</c>) emitting stack bytecode, and its 16-bit evaluator
/// (<c>0x43CF2C</c>). The grammar is reproduced as the executable has it,
/// including its quirks: <c>&amp;&amp;</c> and <c>||</c> share one precedence and
/// associate to the right, addition binds tighter than multiplication, and an
/// unknown name emits nothing.
/// </summary>
public static class TriggerExpression
{
    public const byte UnitType = 0x00, UnitTeam = 0x01, Or = 0x02, And = 0x03, Equal = 0x04, Less = 0x05,
        Greater = 0x06, Literal = 0x07, Clock = 0x08, Multiply = 0x09, Divide = 0x0a, Add = 0x0b, Subtract = 0x0c,
        ScriptArray = 0x0d, SlotHealth = 0x0e, Visible = 0x0f, PlayerStat = 0x10, TypeStat = 0x11, Random = 0x12,
        Modulo = 0x13, NotEqual = 0x14, MineAlive = 0x15, End = 0x16;

    public static byte[] Compile(string text)
    {
        var compiler = new Compiler(text);
        compiler.Expression();
        return compiler.Code.ToArray();
    }

    /// <summary>
    /// Runs compiled bytecode. Values are 16-bit; comparisons are signed. The
    /// word below the native stack is a -1 sentinel (<c>0x43CF4C</c>). The
    /// malformed <c>&amp;&amp;==0</c> of human09's trigger 1 leaves one operand
    /// short, so its last <c>&amp;&amp;</c> reads that sentinel, which keeps the
    /// rest of the chain. Deeper reads would hit unrelated stack memory and
    /// read 0 here; the corpus has none.
    /// </summary>
    public static int Evaluate(ReadOnlySpan<byte> code, ITriggerExpressionContext context)
    {
        Span<short> stack = stackalloc short[264];
        var top = 8;
        stack[top - 1] = -1;
        var pc = 0;
        while (true)
        {
            var op = code[pc++];
            switch (op)
            {
                case UnitType: stack[top++] = (short)(context.Unit?.EntityType ?? 0); break;
                case UnitTeam: stack[top++] = (short)(context.Unit?.Team ?? 0); break;
                case Or: stack[top - 2] = (short)(stack[top - 2] | stack[top - 1]); top--; break;
                case And: stack[top - 2] = (short)(stack[top - 2] & stack[top - 1]); top--; break;
                case Equal: stack[top - 2] = (short)(stack[top - 2] == stack[top - 1] ? 1 : 0); top--; break;
                case Less: stack[top - 2] = (short)(stack[top - 2] < stack[top - 1] ? 1 : 0); top--; break;
                case Greater: stack[top - 2] = (short)(stack[top - 1] < stack[top - 2] ? 1 : 0); top--; break;
                case NotEqual: stack[top - 2] = (short)(stack[top - 2] != stack[top - 1] ? 1 : 0); top--; break;
                case Literal: stack[top++] = (short)(code[pc] | code[pc + 1] << 8); pc += 2; break;
                case Clock: stack[top++] = (short)context.Clock; break;
                case Multiply: stack[top - 2] = (short)(stack[top - 2] * stack[top - 1]); top--; break;
                case Divide: stack[top - 2] = (short)(stack[top - 1] == 0 ? 0 : stack[top - 2] / stack[top - 1]); top--; break;
                case Modulo: stack[top - 2] = (short)(stack[top - 1] == 0 ? 0 : stack[top - 2] % stack[top - 1]); top--; break;
                case Add: stack[top - 2] = (short)(stack[top - 2] + stack[top - 1]); top--; break;
                case Subtract: stack[top - 2] = (short)(stack[top - 2] - stack[top - 1]); top--; break;
                case ScriptArray: stack[top - 1] = (short)context.ScriptWord(stack[top - 1]); break;
                case SlotHealth: stack[top - 2] = (short)context.SlotHealth(stack[top - 2], stack[top - 1]); top--; break;
                case Visible:
                    stack[top - 3] = (short)(context.CellVisible(stack[top - 3], stack[top - 2], stack[top - 1]) ? 1 : 0);
                    top -= 2;
                    break;
                case PlayerStat: stack[top - 2] = (short)context.PlayerStat(stack[top - 2], stack[top - 1]); top--; break;
                case TypeStat:
                    stack[top - 3] = (short)context.TypeStat(stack[top - 3], stack[top - 2], stack[top - 1]);
                    top -= 2;
                    break;
                case Random: stack[top++] = (short)context.NextRandom(); break;
                case MineAlive: stack[top - 2] = (short)(context.MineAlive(stack[top - 2], stack[top - 1]) ? 1 : 0); top--; break;
                case End: return stack[top - 1];
                default: throw new InvalidDataException($"Unknown trigger opcode {op}.");
            }
        }
    }

    /// <summary>Recursive descent mirroring 0x43C5B8 and its tail parsers.</summary>
    private sealed class Compiler(string text)
    {
        private int position;
        private bool lastReadConsumed;
        public List<byte> Code { get; } = [];

        // 0x43C51C reads the next non-blank character (0 at the end); 0x43C534 ungets it.
        private char Next()
        {
            while (position < text.Length && (text[position] == ' ' || text[position] == '\t')) position++;
            lastReadConsumed = position < text.Length;
            return lastReadConsumed ? text[position++] : '\0';
        }

        private void Unget()
        {
            if (lastReadConsumed) position--;
            lastReadConsumed = false;
        }

        private void Emit(byte value) => Code.Add(value);

        private static InvalidDataException Error(string message) => new($"Trigger expression: {message}");

        /// <summary>0x43C5B8: a whole expression, which must end the text.</summary>
        public void Expression()
        {
            Operand();
            MultiplicativeTail();
            ComparisonTail();
            LogicalTail();
            if (Next() != '\0') throw Error("trailing characters");
            Emit(End);
        }

        /// <summary>0x43C600: a parenthesized expression body.</summary>
        private void Grouped()
        {
            Primary();
            AdditiveTail();
            MultiplicativeTail();
            ComparisonTail();
            LogicalTail();
        }

        /// <summary>0x43C96C: a primary with its additive tail (function arguments use this).</summary>
        private void Operand()
        {
            Primary();
            AdditiveTail();
        }

        /// <summary>0x43C988: <c>+</c> / <c>-</c>, right-associative.</summary>
        private void AdditiveTail()
        {
            var c = Next();
            if (c is '+' or '-')
            {
                Primary();
                AdditiveTail();
                Emit(c == '+' ? Add : Subtract);
                return;
            }
            Unget();
        }

        /// <summary>0x43C87C: one <c>*</c>, <c>/</c> or <c>%</c> over an operand.</summary>
        private void MultiplicativeTail()
        {
            var c = Next();
            if (c is '*' or '/' or '%')
            {
                Primary();
                AdditiveTail();
                Emit(c switch { '*' => Multiply, '/' => Divide, _ => Modulo });
                return;
            }
            Unget();
        }

        /// <summary>0x43C724: one comparison; <c>==</c> and <c>!=</c> consume their second character.</summary>
        private void ComparisonTail()
        {
            var c = Next();
            byte op;
            switch (c)
            {
                case '=':
                    if (Next() != '=') Unget();
                    op = Equal;
                    break;
                case '!':
                    Next();
                    op = NotEqual;
                    break;
                case '<': op = Less; break;
                case '>': op = Greater; break;
                default:
                    Unget();
                    return;
            }
            Primary();
            AdditiveTail();
            MultiplicativeTail();
            Emit(op);
        }

        /// <summary>
        /// 0x43C638: <c>&amp;</c>/<c>|</c> (the second character is consumed unread).
        /// The right operand includes the rest of the logical chain, so the
        /// chain associates to the right with one shared precedence.
        /// </summary>
        private void LogicalTail()
        {
            var c = Next();
            if (c is '&' or '|')
            {
                Next();
                Operand();
                MultiplicativeTail();
                ComparisonTail();
                LogicalTail();
                Emit(c == '&' ? And : Or);
                return;
            }
            if (c is ')' or ',' or '\0')
            {
                Unget();
                return;
            }
            throw Error($"unexpected '{c}'");
        }

        private void Arguments(int count)
        {
            if (Next() != '(') throw Error("expected '('");
            for (var index = 0; index < count; index++)
            {
                if (index > 0 && Next() != ',') throw Error("expected ','");
                Operand();
            }
        }

        private void Close()
        {
            if (Next() != ')') throw Error("expected ')'");
        }

        /// <summary>0x43CA3C: literals, groups, and the single-letter names.</summary>
        private void Primary()
        {
            var c = Next();
            if (c is >= '0' and <= '9')
            {
                var value = c - '0';
                while (true)
                {
                    var digit = Next();
                    if (digit is < '0' or > '9')
                    {
                        Unget();
                        break;
                    }
                    value = value * 10 + digit - '0';
                }
                Emit(Literal);
                Emit((byte)value);
                Emit((byte)(value >> 8));
                return;
            }
            switch (c)
            {
                case '(':
                    Grouped();
                    Close();
                    return;
                case 'S': Emit(UnitTeam); return;
                case 't': Emit(UnitType); return;
                case 'c': Emit(Clock); return;
                case 'r': Emit(Random); return;
                case 'b': Arguments(2); Close(); Emit(SlotHealth); return;
                case 'm': Arguments(2); Close(); Emit(MineAlive); return;
                case 'u': Arguments(1); Close(); Emit(ScriptArray); return;
                case 'v': Arguments(3); Close(); Emit(Visible); return;
                case 's':
                    Arguments(2);
                    var separator = Next();
                    if (separator == ')')
                    {
                        Emit(PlayerStat);
                        return;
                    }
                    if (separator != ',') throw Error("expected ',' or ')'");
                    Operand();
                    Close();
                    Emit(TypeStat);
                    return;
                default:
                    // Any other character is consumed and emits nothing.
                    return;
            }
        }
    }
}
