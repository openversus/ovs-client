using OpenVersus.Game;
using OpenVersus.Memory;

namespace OpenVersus.Tests;

/// <summary>
/// The object finder against a fake game. The fixture is built from the code's own offset
/// constants, so these prove the comparisons (which candidate wins, what stops a walk), not
/// the offsets; only the running game checks those.
/// </summary>
public class ObjectFinderTests
{
    [Fact]
    public void FakeMemoryRefusesPartialRangesAndAddressZero()
    {
        var memory = new FakeMemory();
        memory.Alloc(0x1000, 16);
        memory.Write((nint)0x1008, 0x1122334455667788L);
        Assert.True(memory.TryRead((nint)0x1008, out long value));
        Assert.Equal(0x1122334455667788L, value);
        Assert.False(memory.TryRead((nint)0x100C, out long _), "range runs past the block");
        Assert.False(memory.TryRead((nint)0x0FFF, out byte _), "range starts before the block");
        Assert.False(memory.TryRead(0, out byte _));
        Assert.False(memory.TryRead((nint)0x2000, out byte _));
    }

    [Fact]
    public void ObjectArrayOpensAValidArrayAndRejectsABadOne()
    {
        var game = new FakeGame();
        var array = ObjectArray.Open(game.Image, game.Memory, game.Names, game.Log);
        Assert.NotNull(array);
        Assert.Equal(1024, array!.Count);
        Assert.Equal(1, array.Chunks);
        Assert.Equal(1024, array.Objects().Count());
        Assert.Equal(game.ClassClass, array[0]);
        Assert.Equal(0, array[5000]);

        game.CorruptChunkCount(2);
        Assert.Null(ObjectArray.Open(game.Image, game.Memory, game.Names, game.Log));
        Assert.Contains(game.Log.Lines, l => l.Contains("does not look like a chunked array"));

        // An entry whose name the engine cannot print means the layout is not what we think.
        var unresolvable = new FakeGame();
        unresolvable.Memory.Write(unresolvable.ClassClass + Mvs.ObjectNamePrivate, 999);
        Assert.Null(ObjectArray.Open(unresolvable.Image, unresolvable.Memory, unresolvable.Names, unresolvable.Log));
        Assert.Contains(unresolvable.Log.Lines, l => l.Contains("does not resolve"));

        // A vtable outside the image, likewise.
        var stray = new FakeGame();
        stray.Memory.Write(stray.ClassClass + Mvs.ObjectVTable, 0x7000000000L);
        Assert.Null(ObjectArray.Open(stray.Image, stray.Memory, stray.Names, stray.Log));
        Assert.Contains(stray.Log.Lines, l => l.Contains("no vtable in the image"));
    }

    /// <summary>
    /// Liveness through the array: an object names its own slot, so two reads say whether the
    /// engine still lists it. A freed object keeps its bytes and loses its slot. If the index
    /// does not name the slot, the check is switched off and says so, and the array stays usable.
    /// </summary>
    [Fact]
    public void AFreedObjectIsNotLiveAndAnUntrustedIndexTurnsTheCheckOff()
    {
        var game = new FakeGame();
        nint uclass = game.AddClass("PfgNetcodeSession");
        nint session = game.AddInstance(uclass, "Session");
        var finder = game.Finder();
        Assert.Equal(session, finder.FindInstanceOfClass(uclass));
        Assert.True(finder.UsesObjectArray);
        Assert.True(finder.IsLive(session));
        Assert.True(finder.IsLive(game.ClassClass));

        game.RemoveObject(session);
        Assert.True(ObjectHeader.TryRead(game.Memory, session, out var header), "a freed object's bytes still read");
        Assert.Equal(uclass, header.ClassPrivate);
        Assert.False(finder.IsLive(session));
        Assert.Equal(0, finder.FindInstanceOfClass(uclass));
        Assert.DoesNotContain(game.Log.Lines, l => l.Contains("liveness checks through the array are off"));

        // The same game with an object whose index does not name its slot.
        var odd = new FakeGame();
        nint oddClass = odd.AddClass("PfgNetcodeSession");
        nint oddSession = odd.AddInstance(oddClass, "Session");
        odd.Memory.Write(odd.ClassClass + Mvs.ObjectInternalIndex, 7);
        var oddFinder = odd.Finder();
        Assert.Equal(oddSession, oddFinder.FindInstanceOfClass(oddClass));
        Assert.True(oddFinder.UsesObjectArray);
        Assert.Contains(odd.Log.Lines, l => l.Contains("slot 0 has internal index 7; liveness checks through the array are off"));
        odd.RemoveObject(oddSession);
        Assert.True(oddFinder.IsLive(oddSession), "without a trusted index the check cannot say no");
    }

    [Fact]
    public void TheChunkTablePointerIsDecodedWithTheBuildsKey()
    {
        // A table pointer stored without the key decodes to garbage, and the array must refuse it.
        var game = new FakeGame();
        nint global = game.Image.Address(ObjectArray.GUObjectArrayRva);
        game.Memory.TryRead(global + ObjectArray.ObjectsOffset, out ulong encoded);
        game.Memory.Write(global + ObjectArray.ObjectsOffset, encoded ^ ObjectArray.ObjectsKey);
        Assert.Null(ObjectArray.Open(game.Image, game.Memory, game.Names, game.Log));
        game.Memory.Write(global + ObjectArray.ObjectsOffset, encoded);
        Assert.NotNull(ObjectArray.Open(game.Image, game.Memory, game.Names, game.Log));
    }

    [Fact]
    public void AFailedOpenSaysWhatBytesItSaw()
    {
        // Through the opener alone: a finder whose array fails falls to the heap scanner, which needs Windows.
        var game = new FakeGame();
        game.CorruptChunkCount(2);
        Assert.Null(ObjectArray.Open(game.Image, game.Memory, game.Names, game.Log));
        Assert.Contains(game.Log.Lines, l => l.Contains("bytes at 0x"));
    }

    [Fact]
    public void TheArraySeesObjectsCreatedAfterItWasOpened()
    {
        var game = new FakeGame();
        var array = ObjectArray.Open(game.Image, game.Memory, game.Names, game.Log);
        Assert.NotNull(array);
        int before = array!.Count;
        nint late = game.AddClass("LateArrival");
        Assert.Equal(before + 1, array.Count);
        Assert.Contains(late, array.Objects());
    }

    [Fact]
    public void FindClassPicksTheUClassAmongObjectsSharingItsName()
    {
        var game = new FakeGame();
        nint other = game.AddClass("SomethingElse");
        // Same name, three shapes: an instance, an object of an unrelated class, and the class itself.
        game.AddInstance(other, "PfgNetcodeSession");
        game.AddObject("PfgNetcodeSession", game.GenericVTable, other, 0);
        nint sessionClass = game.AddClass("PfgNetcodeSession");
        var finder = game.Finder();

        Assert.Equal(sessionClass, finder.FindClass("PfgNetcodeSession"));
        Assert.Equal(sessionClass, finder.FindClass("PfgNetcodeSession"));
        Assert.Equal(0, finder.FindClass("NoSuchClass"));
        Assert.True(finder.UsesObjectArray);

        // A Blueprint-generated class (the "_C" ones) is a class too; an instance named like one is not.
        nint blueprint = game.AddBlueprintClass("MatchPlayerData_C");
        game.AddInstance(other, "MatchPlayerData_C");
        Assert.Equal(blueprint, finder.FindClass("MatchPlayerData_C"));
    }

    [Fact]
    public void FindInstanceSkipsDefaultObjectsAndFollowsInheritance()
    {
        var game = new FakeGame();
        nint baseClass = game.AddClass("PfgNetcodeSession");
        nint derived = game.AddClass("PfgNetcodeSessionDerived", super: baseClass);
        nint unrelated = game.AddClass("Unrelated");
        game.AddInstance(baseClass, "Default", defaultObject: true);
        game.AddInstance(unrelated, "Other");
        nint live = game.AddInstance(derived, "Live");
        var finder = game.Finder();

        Assert.Equal(live, finder.FindInstanceOfClass(baseClass));
        Assert.Equal(0, finder.FindInstanceOfClass(unrelated == 0 ? 1 : game.AddClass("Empty")));
        Assert.Equal(0, finder.FindInstanceOfClass(0));

        Assert.True(ObjectFinder.Inherits(game.Memory, derived, baseClass));
        Assert.False(ObjectFinder.Inherits(game.Memory, baseClass, derived));
        // A SuperStruct pointer into nothing ends the walk instead of reading zeros.
        nint broken = game.AddClass("Broken", super: (nint)0x300000000);
        Assert.False(ObjectFinder.Inherits(game.Memory, broken, baseClass));
    }

    [Fact]
    public void FindFunctionRequiresAUFunctionVTableAndTheDeclaringClass()
    {
        var game = new FakeGame();
        nint state = game.AddClass("MvsPreMatchTransitioningToGameplayState");
        nint elsewhere = game.AddClass("SomeOtherState");
        game.AddFunction("HandleTransitionToGameplayFailed", game.GenericVTable, state);            // not a UFunction
        game.AddFunction("HandleTransitionToGameplayFailed", game.NativeFunctionVTable, elsewhere); // wrong class
        nint wanted = game.AddFunction("HandleTransitionToGameplayFailed", game.BlueprintFunctionVTable, state);
        var finder = game.Finder();

        Assert.Equal(wanted, finder.FindFunction("MvsPreMatchTransitioningToGameplayState", "HandleTransitionToGameplayFailed", out nint owner));
        Assert.Equal(state, owner);
        Assert.Equal(0, finder.FindFunction("SomeOtherState", "NoSuchFunction", out _));
        Assert.Equal(0, finder.FindFunction("NoSuchClass", "HandleTransitionToGameplayFailed", out _));
    }

    [Fact]
    public void DescribeDecodesTheParameterChainAndStopsAtAnUnreadableLink()
    {
        var game = new FakeGame();
        nint owner = game.AddClass("Owner");
        nint ret = game.AddProperty("ReturnValue", "BoolProperty", ReflectedParameter.CPF_Parm | ReflectedParameter.CPF_ReturnParm | ReflectedParameter.CPF_OutParm, offset: 8, size: 1);
        nint notAParm = game.AddProperty("Internal", "IntProperty", flags: 0, offset: 4, size: 4, next: ret);
        nint first = game.AddProperty("Value", "IntProperty", ReflectedParameter.CPF_Parm, offset: 0, size: 4, arrayDim: 2, next: notAParm);
        nint fn = game.AddFunction("DoThing", game.NativeFunctionVTable, owner, flags: 0x04020401, numParms: 2, parmsSize: 12, returnOffset: 8, nativeFunc: (nint)0x140001234, firstProperty: first);

        var sig = Reflection.Describe(game.Memory, game.Names, fn, "Owner", "DoThing", owner);
        Assert.Equal(0x04020401u, sig.FunctionFlags);
        Assert.Equal(2, sig.NumParms);
        Assert.Equal(12, sig.ParmsSize);
        Assert.Equal(8, sig.ReturnValueOffset);
        Assert.Equal((nint)0x140001234, sig.NativeFunc);
        Assert.Equal(2, sig.Parameters.Count);
        Assert.Equal("IntProperty Value @+0x0 (4x2)", sig.Parameters[0].ToString());
        Assert.Equal("return BoolProperty ReturnValue @+0x8 (1)", sig.Parameters[1].ToString());
        Assert.Contains("Owner::DoThing(", sig.ToString());

        // No return value is reported as -1, and a chain that runs into nothing stops there.
        nint dangling = game.AddProperty("Lonely", "IntProperty", ReflectedParameter.CPF_Parm, offset: 0, size: 4, next: (nint)0x300000000);
        nint fn2 = game.AddFunction("Other", game.NativeFunctionVTable, owner, numParms: 1, parmsSize: 4, firstProperty: dangling);
        var sig2 = Reflection.Describe(game.Memory, game.Names, fn2, "Owner", "Other", owner);
        Assert.Equal(-1, sig2.ReturnValueOffset);
        Assert.Single(sig2.Parameters);
    }
}
