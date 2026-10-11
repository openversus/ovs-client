using OpenVersus.Memory;

namespace OpenVersus.Game;

/// <summary>
/// Every game function the client knows, by name. Hooks register what they resolve; anything
/// else can look a function up here, by name, and call it through its address. The names are
/// the C++ globals' names so the two clients' logs read the same.
/// </summary>
public static class GameFunctions
{
    private static readonly Dictionary<string, GameFunction> s_byName = new(StringComparer.Ordinal);
    private static readonly object s_lock = new();

    /// <summary>The RVA of UObject::ProcessEvent in the final build (Dumper-7 OffsetsInfo.json).</summary>
    public const uint ProcessEventRva = 0x02D3D810;
    /// <summary>UMvsNotificationManager getter in the final build, used if its pattern fails.</summary>
    public const uint GetNotificationManagerRva = 0x028B1D60;

    /// <summary>Adds <paramref name="function"/> under its name, replacing any earlier function of that name, and returns it.</summary>
    public static GameFunction Register(GameFunction function)
    {
        lock (s_lock)
        {
            s_byName[function.Name] = function;
        }

        return function;
    }

    /// <summary>Registers a function found in native code, with <paramref name="nativeDeclaration"/> as its signature.</summary>
    public static GameFunction Register(string name, nint address, FunctionSource source, string origin, string nativeDeclaration, GameImage? image = null) =>
        Register(new GameFunction
        {
            Name = name,
            Address = address,
            Source = source,
            Origin = origin,
            Image = image,
            Signature = new FunctionSignature { Native = new NativeSignature(nativeDeclaration) },
        });

    /// <summary>The destination of the call or jmp at <paramref name="instruction"/>, registered under <paramref name="name"/>.</summary>
    public static GameFunction FromCallSite(string name, nint instruction, string origin, string nativeDeclaration, GameImage image) =>
        Register(name, CallSite.Destination(instruction), FunctionSource.CallSite, origin, nativeDeclaration, image);

    /// <summary>The function at <paramref name="rva"/> in <paramref name="image"/>, registered under <paramref name="name"/>.</summary>
    public static GameFunction FromRva(string name, uint rva, string nativeDeclaration, GameImage image) =>
        Register(name, image.Address(rva), FunctionSource.Rva, $"rva 0x{rva:X}", nativeDeclaration, image);

    /// <summary>The function registered under <paramref name="name"/>, or null.</summary>
    public static GameFunction? Find(string name)
    {
        lock (s_lock)
        {
            return s_byName.TryGetValue(name, out var f) ? f : null;
        }
    }

    /// <summary>The address by name, or 0. For code that would rather not branch on a null.</summary>
    public static nint Address(string name) => Find(name)?.Address ?? 0;

    /// <summary>A snapshot of every registered function, ordered by name.</summary>
    public static IReadOnlyList<GameFunction> All
    {
        get
        {
            lock (s_lock)
            {
                return s_byName.Values.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            }
        }
    }
}
