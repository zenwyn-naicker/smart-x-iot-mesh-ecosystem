namespace SmartX.Api.Models;

public readonly record struct PowerReading(int Watts)
{
    public static PowerReading operator +(
        PowerReading left,
        PowerReading right)
    {
        return new PowerReading(left.Watts + right.Watts);
    }

    public static PowerReading operator -(
        PowerReading left,
        PowerReading right)
    {
        return new PowerReading(left.Watts - right.Watts);
    }

    public static bool operator >(
        PowerReading left,
        PowerReading right)
    {
        return left.Watts > right.Watts;
    }

    public static bool operator <(
        PowerReading left,
        PowerReading right)
    {
        return left.Watts < right.Watts;
    }

    public static bool operator >=(
        PowerReading left,
        PowerReading right)
    {
        return left.Watts >= right.Watts;
    }

    public static bool operator <=(
        PowerReading left,
        PowerReading right)
    {
        return left.Watts <= right.Watts;
    }
}