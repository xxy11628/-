using System.Numerics;

namespace UltrasoundHost.Contracts;

/// <summary>
/// A probe is a list of element positions, not "count and pitch". Linear, matrix, annular and curved
/// arrays are just different generators of that list, so delay laws and imaging grids written
/// against it work for any of them. Coordinates are metres: origin at the centre of the contact
/// face, x along the array, y across it, z pointing into the workpiece.
/// </summary>
public sealed class ProbeModel
{
    public ProbeModel(ProbeTopologies topology, Vector3[] positions, Vector3[] normals)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(normals);
        if (positions.Length < 1)
        {
            throw new ArgumentException("a probe needs at least one element", nameof(positions));
        }

        if (normals.Length != positions.Length)
        {
            throw new ArgumentException("normals must have one entry per element", nameof(normals));
        }

        Topology = topology;
        Positions = positions;
        Normals = normals;
    }

    public ProbeTopologies Topology { get; }

    public IReadOnlyList<Vector3> Positions { get; }

    public IReadOnlyList<Vector3> Normals { get; }

    public int ElementCount => Positions.Count;

    public static ProbeModel Single()
    {
        return new ProbeModel(ProbeTopologies.Single, new[] { Vector3.Zero }, new[] { Vector3.UnitZ });
    }

    /// <summary>A linear array centred on the origin.</summary>
    public static ProbeModel Linear(int count, double pitchMeters)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (!(pitchMeters > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(pitchMeters));
        }

        var positions = new Vector3[count];
        var normals = new Vector3[count];
        double centre = (count - 1) / 2.0;
        for (int i = 0; i < count; i++)
        {
            positions[i] = new Vector3((float)((i - centre) * pitchMeters), 0f, 0f);
            normals[i] = Vector3.UnitZ;
        }

        return new ProbeModel(ProbeTopologies.Linear, positions, normals);
    }

    /// <summary>A rows x cols matrix array centred on the origin, row-major element order.</summary>
    public static ProbeModel Matrix(int rows, int cols, double pitchXMeters, double pitchYMeters)
    {
        if (rows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }

        if (cols < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(cols));
        }

        if (!(pitchXMeters > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(pitchXMeters));
        }

        if (!(pitchYMeters > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(pitchYMeters));
        }

        var positions = new Vector3[checked(rows * cols)];
        var normals = new Vector3[positions.Length];
        double centreX = (cols - 1) / 2.0;
        double centreY = (rows - 1) / 2.0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int index = (r * cols) + c;
                positions[index] = new Vector3((float)((c - centreX) * pitchXMeters), (float)((r - centreY) * pitchYMeters), 0f);
                normals[index] = Vector3.UnitZ;
            }
        }

        return new ProbeModel(ProbeTopologies.Matrix, positions, normals);
    }
}
