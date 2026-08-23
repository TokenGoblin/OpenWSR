using System.Runtime.CompilerServices;

// The Julian day conversion is an implementation detail of the solar series, but it is the
// one step with a definition rather than a derivation — J2000.0 is 2451545.0 exactly — so it
// is worth pinning directly rather than only through the angles it feeds.
[assembly: InternalsVisibleTo("OpenWSR.Geo.Tests")]
