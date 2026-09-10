using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class CellDisplaceLawTests {
    private static SdfProgram Program(SdfCellMode mode, float randomness, uint seed, bool separateChain = false) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new(Vector3.One));
        builder.Plane(Vector3.UnitY, 0f, material);
        if (separateChain) { builder.ResetPoint(); }
        return builder.CellDisplace(1f, 0.5f, seed, mode, randomness).Build();
    }
    // Independent double-precision geometry, with the shared integer hash supplying only feature identity.
    private static double Reference(double x, double y, double z, uint seed, SdfCellMode mode, double randomness, int radius) {
        var cx=(int)Math.Floor(x); var cy=(int)Math.Floor(y); var cz=(int)Math.Floor(z);
        var first=double.PositiveInfinity; var second=double.PositiveInfinity;
        for(var iz=-radius;iz<=radius;iz++) { for(var iy=-radius;iy<=radius;iy++) { for(var ix=-radius;ix<=radius;ix++) {
            var h=Pcg3dLatticeNoise.Pcg3d(unchecked((uint)(cx+ix))^seed, unchecked((uint)(cy+iy))^(seed^0x9E3779B9u), unchecked((uint)(cz+iz))^(seed^0x85EBCA77u));
            var dx=cx+ix+.5+randomness*((h.X>>16)/65536d-.5)-x;
            var dy=cy+iy+.5+randomness*((h.Y>>16)/65536d-.5)-y;
            var dz=cz+iz+.5+randomness*((h.Z>>16)/65536d-.5)-z;
            var distance=Math.Sqrt(dx*dx+dy*dy+dz*dz);
            if(distance<first) { second=first; first=distance; } else if(distance<second) { second=distance; }
        }
        } }
        return mode==SdfCellMode.F1 ? first : second-first;
    }
    [Theory]
    [InlineData(SdfCellMode.F1, 0.46f)]
    [InlineData(SdfCellMode.F2MinusF1, 0.2f)]
    public void DenseNeighborhoodSweepMatches125CellsAndTheFixedInterpreter(SdfCellMode mode, float randomness) {
        // Includes negative cells and every cell face, edge, and corner; four unrelated PCG seeds.
        foreach(var seed in new uint[]{0,1,0xDEADBEEF,uint.MaxValue}) {
            var field=new SdfFieldEvaluator(Program(mode,randomness,seed));
            for(var iz=-16;iz<=16;iz++) { for(var iy=-16;iy<=16;iy++) { for(var ix=-16;ix<=16;ix++) {
                var x=ix/16d; var y=iy/16d; var z=iz/16d;
                var expected=Reference(x,y,z,seed,mode,randomness,2);
                Assert.Equal(expected,Reference(x,y,z,seed,mode,randomness,1),12);
                Assert.True(field.TryDistance(FixedPosition.FromLocal(new(FixedQ4816.FromDouble(x),FixedQ4816.FromDouble(y),FixedQ4816.FromDouble(z))),out var actual,out _));
                Assert.InRange((double)actual,y+.5*(expected-.5)-0.00004,y+.5*(expected-.5)+0.00004);
            }
        } }
        }
    }
    [Theory]
    [InlineData(SdfCellMode.F1, 0.46f)]
    [InlineData(SdfCellMode.F2MinusF1, 0.2f)]
    public void NeighborhoodCeilingHasAnAnalyticContainmentMargin(SdfCellMode mode,float randomness) {
        var d=(double)randomness/2;
        var contained=mode==SdfCellMode.F1 ? Math.Sqrt(3)*(.5+d) : Math.Sqrt((1+d)*(1+d)+2*(.5+d)*(.5+d));
        Assert.True(contained < 1.5-d);
    }
    [Theory]
    [InlineData(SdfCellMode.F1, 0.46f)]
    [InlineData(SdfCellMode.F2MinusF1, 0.2f)]
    public void StepFactorCoversMeasuredGradientsAndShapeFreeFieldChains(SdfCellMode mode,float randomness) {
        var factor=1d/Program(mode,randomness,123).StepScale;
        Assert.InRange(1d/Program(mode,randomness,123,true).StepScale,factor-0.00001,factor+0.00001);
        const double e=0.00001;
        double Field(double x,double y,double z)=>y+.5*(Reference(x,y,z,123,mode,randomness,2)-.5);
        for(var z=-4;z<=4;z++) { for(var y=-4;y<=4;y++) { for(var x=-4;x<=4;x++) {
            var px=x*.23; var py=y*.27; var pz=z*.29;
            var gx=(Field(px+e,py,pz)-Field(px-e,py,pz))/(2*e);
            var gy=(Field(px,py+e,pz)-Field(px,py-e,pz))/(2*e);
            var gz=(Field(px,py,pz+e)-Field(px,py,pz-e))/(2*e);
            Assert.True(Math.Sqrt(gx*gx+gy*gy+gz*gz)<=factor+0.00001);
        } } }
    }
    [Theory]
    [InlineData(0.25f, false)]
    [InlineData(0.25f, true)]
    [InlineData(3f, false)]
    [InlineData(3f, true)]
    public void ScaleChangesTheSamplingDerivativeWithoutScalingFieldAmplitude(float scale, bool separateChain) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new(Vector3.One));
        if (!separateChain) { builder.Scale(new(scale)); }
        builder.Plane(Vector3.UnitY, 0f, material);
        if (separateChain) { builder.ResetPoint().Scale(new(scale)); }
        var program = builder.CellDisplace(1f, .5f, 0, SdfCellMode.F1, .46f).Build();
        Assert.InRange(1f / program.StepScale, 1f + .5f / scale - .00001f, 1f + .5f / scale + .00001f);
    }
    [Fact]
    public void DiscontinuousSamplingRequiresARestoredFrame() {
        var builder = new SdfProgramBuilder().Repeat(Vector3.One).CellDisplace(1f,.5f,0,SdfCellMode.F1,.46f);
        Assert.Throws<ArgumentException>(() => builder.Build());
    }
    [Fact]
    public void BothAdmissionDoorsRefuseInvalidParameters() {
        var valid=new SdfCellDisplacement(1f,.5f,0u,SdfCellMode.F1,.2f);
        foreach(var p in new[]{valid with{Frequency=0},valid with{Frequency=float.NaN},valid with{Amplitude=-1},valid with{Amplitude=float.PositiveInfinity},valid with{Mode=(SdfCellMode)2},valid with{Randomness=-.1f},valid with{Randomness=.47f},valid with{Mode=SdfCellMode.F2MinusF1,Randomness=.21f},valid with{Randomness=float.NaN}}) {
            Assert.ThrowsAny<ArgumentException>(()=>new SdfProgramBuilder().CellDisplace(p.Frequency,p.Amplitude,p.Seed,p.Mode,p.Randomness));
            var program=Program(SdfCellMode.F1,.2f,0);
            var instructions=program.Instructions.ToArray();
            instructions[^1]=new(SdfOp.CellDisplace,p.Seed,(uint)p.Mode,0,new(p.Frequency,p.Amplitude,p.Randomness,0),Vector4.Zero);
            Assert.ThrowsAny<ArgumentException>(()=>new SdfProgram(instructions,[new(Vector3.One)]));
        }
    }
}
