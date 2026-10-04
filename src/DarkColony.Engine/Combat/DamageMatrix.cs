using System.Globalization;

namespace DarkColony.Engine.Combat;

public sealed class DamageMatrix
{
    public const int WeaponClassCount = 9;
    public const int ArmorClassCount = 10;

    private readonly int[,] _percentages;

    private DamageMatrix(int[,] percentages)
    {
        _percentages = percentages;
    }

    public int this[int weaponClass, int armorClass] => _percentages[weaponClass, armorClass];

    public static DamageMatrix Load(string path) => Parse(File.ReadAllText(path));

    public static DamageMatrix Parse(string text)
    {
        var values = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Select(line => line.Trim())
            .Select(line => line.Split('%', 2)[0].Trim())
            .Where(line => line.Length != 0)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();

        if (values.Length < 2 || values[0].Length != 1 || values[1].Length != 1)
        {
            throw new InvalidDataException("mbullet.txt has no matrix dimensions.");
        }

        var columns = values[0][0];
        var rows = values[1][0];
        if (rows != WeaponClassCount || columns != ArmorClassCount || values.Length != rows + 2)
        {
            throw new InvalidDataException($"Expected a {WeaponClassCount}x{ArmorClassCount} damage matrix.");
        }

        var matrix = new int[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            if (values[row + 2].Length != columns)
            {
                throw new InvalidDataException($"Damage-matrix row {row} has the wrong width.");
            }

            for (var column = 0; column < columns; column++)
            {
                matrix[row, column] = values[row + 2][column];
            }
        }

        return new DamageMatrix(matrix);
    }

    /// <summary>
    /// The executable's table entry (loader <c>0x43B259</c>): the text
    /// percentage times 0.01 times 256, truncated, i.e. an 8.8 factor.
    /// </summary>
    public int NativeFactor(int weaponClass, int armorClass) => (int)(this[weaponClass, armorClass] * 0.01 * 256.0);

    public int CalculateBaseDamage(int rawDamage, int weaponClass, int armorClass) =>
        CalculateNativeDamage(rawDamage, weaponClass, armorClass);

    /// <summary>
    /// <c>0x441930</c>: <c>((factor * raw) &gt;&gt; 8) * multiplier &gt;&gt; 8</c>, then
    /// times the target's armor-level factor &gt;&gt; 8, then three quarters
    /// when asked (<c>(d * 3) &gt;&gt; 2</c>). Direct hits pass multiplier 0x100
    /// (or an inspiring commander's factor) and the shooter's day/night
    /// penalty; splash passes its pattern weight and no penalty.
    /// </summary>
    public int CalculateNativeDamage(int rawDamage, int weaponClass, int armorClass, int multiplier = 0x100,
        int armorMultiplier = 0x100, bool reduceToThreeQuarters = false)
    {
        if (rawDamage < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rawDamage));
        }

        var damage = checked(NativeFactor(weaponClass, armorClass) * rawDamage) >> 8;
        damage = checked(damage * multiplier) >> 8;
        damage = checked(damage * armorMultiplier) >> 8;
        return reduceToThreeQuarters ? (damage * 3) >> 2 : damage;
    }
}
