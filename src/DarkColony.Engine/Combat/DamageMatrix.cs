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

    public static DamageMatrix Load(string path)
    {
        var values = File.ReadLines(path)
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
        if (rawDamage < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rawDamage));
        }

        return checked(rawDamage * this[weaponClass, armorClass] / 100);
    }
}
