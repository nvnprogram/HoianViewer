using System;
using System.Threading;
using bottlenoselabs.C2CS.Runtime;
using Tracy;

// Runtime target of the woven instrumentation. Compiled directly into PlayerViewer under
// -p:Tracy=true (see PlayerViewer.csproj) and called by the IL the weaver injects. Marked
// [NoProfile] so the weaver — which also processes PlayerViewer.dll — never wraps these methods
// (that would recurse: a zone around Begin calls Begin...).
namespace TracyWeaver.Runtime
{
    /// <summary>
    /// Each eligible method gets <c>ctx = TracyWeave.Begin("Type.Method")</c> at entry and
    /// <c>TracyWeave.End(ctx)</c> in an injected <c>finally</c>; the configured frame method
    /// also gets <see cref="FrameMark"/>. Threads name themselves on their first zone.
    /// </summary>
    [NoProfile]
    public static class TracyWeave
    {
        /// <summary>Opaque zone handle passed from <see cref="Begin"/> to <see cref="End"/>.</summary>
        public readonly struct Ctx
        {
            internal readonly PInvoke.TracyCZoneCtx C;

            internal Ctx(PInvoke.TracyCZoneCtx c)
            {
                C = c;
            }
        }

        [ThreadStatic]
        static bool _named;

        static void NameThread()
        {
            var t = Thread.CurrentThread;
            string name = string.IsNullOrEmpty(t.Name) ? "Thread " + t.ManagedThreadId : t.Name;
            //Tracy keeps this pointer for the process lifetime, so it is intentionally not freed.
            PInvoke.TracySetThreadName(CString.FromString(name));
        }

        /// <summary>Opens a zone named <paramref name="zone"/> and returns its handle.</summary>
        public static Ctx Begin(string zone)
        {
            if (!_named)
            {
                _named = true;
                NameThread();
            }
            //AllocSrcloc copies the strings, so the temporary can be freed right after.
            var s = CString.FromString(zone);
            ulong srcloc = PInvoke.TracyAllocSrcloc(
                0,
                s,
                (ulong)zone.Length,
                s,
                (ulong)zone.Length,
                0
            );
            var ctx = PInvoke.TracyEmitZoneBeginAlloc(srcloc, 1);
            s.Dispose();
            return new Ctx(ctx);
        }

        /// <summary>Closes a zone opened by <see cref="Begin"/>.</summary>
        public static void End(Ctx ctx) => PInvoke.TracyEmitZoneEnd(ctx.C);

        /// <summary>Marks a frame boundary (Tracy's default frame timeline).</summary>
        public static void FrameMark() => PInvoke.TracyEmitFrameMark(default);
    }

    /// <summary>Apply to a method or type to exclude it (and its members) from weaving.</summary>
    [AttributeUsage(
        AttributeTargets.Method
            | AttributeTargets.Constructor
            | AttributeTargets.Class
            | AttributeTargets.Struct
    )]
    public sealed class NoProfileAttribute : Attribute { }
}
