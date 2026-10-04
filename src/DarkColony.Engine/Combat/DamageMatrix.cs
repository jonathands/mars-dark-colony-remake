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

    public int CalculateBaseDamage(int rawDamage, int weaponClass, int armorClass)
    {
        return CalculateNativeDamage(rawDamage, weaponClass, armorClass, reduceToThreeQuarters: false);
    }

    /// <summary>
    /// Applies the executable damage helper's optional caller-controlled
    /// three-quarter branch. The branch is intentionally opt-in: static
    /// callers expose the flag, but its mapping to a port command/projectile
    /// is not yet recovered. Keeping it here preserves the exact arithmetic
    /// for the eventual call-site mapping without changing ordinary impacts.
    /// </summary>
    public int CalculateNativeDamage(int rawDamage, int weaponClass, int armorClass, bool reduceToThreeQuarters)
    {
        if (rawDamage < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rawDamage));
        }

        var damage = checked(rawDamage * this[weaponClass, armorClass] / 100);
        // dc16.exe 0x441c80 uses (damage * 3) >> 2, i.e. signed arithmetic
        // shift after the multiply, rather than a floating-point fraction.
        return reduceToThreeQuarters ? (damage * 3) >> 2 : damage;
    }
}
