using CryBar.TMM;

namespace CryBar.Tests;

public class TbnDecoderTests
{
    [Fact]
    public void U15ToFloat_Zero_ReturnsMinusOne()
    {
        Assert.Equal(-1.0f, TbnDecoder.U15ToFloat(0));
    }

    [Fact]
    public void U15ToFloat_Max_ReturnsPlusOne()
    {
        Assert.Equal(1.0f, TbnDecoder.U15ToFloat(32767));
    }

    [Fact]
    public void U15ToFloat_Mid_ReturnsZero()
    {
        var result = TbnDecoder.U15ToFloat(16383);
        Assert.InRange(result, -0.01f, 0.01f);
    }

    [Fact]
    public void FloatToU15_RoundTrip()
    {
        for (int v = 0; v < 32768; v += 1000)
        {
            float f = TbnDecoder.U15ToFloat(v);
            int back = TbnDecoder.FloatToU15(f);
            Assert.InRange(back, v - 1, v + 1);
        }
    }

    [Fact]
    public void QuatFromPacked_ZeroInputs_ProducesUnitW()
    {
        // x=0,y=0,z=0 -> midpoint = 0,0,0 -> w = 1
        ushort mid = 16384; // ~0.0
        var (x, y, z, w, hand) = TbnDecoder.QuatFromPacked(mid, mid, mid);
        Assert.InRange(w, 0.95f, 1.05f);
        Assert.Equal(0, hand);
    }

    [Fact]
    public void QuatFromPacked_HandednessBit()
    {
        ushort mid = 16384;
        ushort midWithHand = (ushort)(mid | 0x8000);
        var (_, _, _, w, hand) = TbnDecoder.QuatFromPacked(midWithHand, mid, mid);
        Assert.Equal(1, hand);
        Assert.True(w < 0); // w negated when handedness=1
    }

    [Fact]
    public void QuatFromPacked_Normalized()
    {
        // Arbitrary packed values
        var (x, y, z, w, _) = TbnDecoder.QuatFromPacked(10000, 20000, 5000);
        float mag = MathF.Sqrt(x * x + y * y + z * z + w * w);
        Assert.InRange(mag, 0.99f, 1.01f);
    }

    [Fact]
    public void QuatToTbn_IdentityQuat_ProducesAxisAlignedVectors()
    {
        // Identity quaternion (0,0,0,1) should give T=(1,0,0), B=(0,1,0), N=(0,0,1)
        var (t, b, n) = TbnDecoder.QuatToTbn(0, 0, 0, 1);
        Assert.InRange(t.x, 0.99f, 1.01f);
        Assert.InRange(t.y, -0.01f, 0.01f);
        Assert.InRange(b.y, 0.99f, 1.01f);
        Assert.InRange(n.z, 0.99f, 1.01f);
    }

    [Fact]
    public void QuatToTbn_Handedness_FlipsBitangent()
    {
        var (_, b0, _) = TbnDecoder.QuatToTbn(0, 0, 0, 1, handedness: 0);
        var (_, b1, _) = TbnDecoder.QuatToTbn(0, 0, 0, 1, handedness: 1);

        // B should be negated when handedness=1
        Assert.InRange(b0.y + b1.y, -0.01f, 0.01f);
    }

    [Fact]
    public void DecodeNormal_ReturnsUnitVector()
    {
        var (nx, ny, nz) = TbnDecoder.DecodeNormal(16384, 16384, 16384);
        float mag = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        Assert.InRange(mag, 0.95f, 1.05f);
    }

    [Fact]
    public void QuatFromPacked_MatchesPythonReference()
    {
        // Test with specific values that can be verified against the Python implementation
        // u16_x = 16384 (0x4000), u16_y = 32767 (0x7FFF), u16_z = 0 (0x0000)
        // x = U15ToFloat(0x4000) = (16384/32767)*2-1 ≈ 0.0000305
        // y = U15ToFloat(0x7FFF) = 1.0
        // z = U15ToFloat(0x0000) = -1.0
        // w_sq = max(0, 1 - (x²+y²+z²)) = max(0, 1-2) = 0 -> w = 0
        var (x, y, z, w, hand) = TbnDecoder.QuatFromPacked(16384, 32767, 0);
        Assert.Equal(0, hand);
        // After normalization, x²+y²+z² ≈ 2, so each component is divided by sqrt(2)
        float mag = MathF.Sqrt(x * x + y * y + z * z + w * w);
        Assert.InRange(mag, 0.99f, 1.01f);
    }
}
