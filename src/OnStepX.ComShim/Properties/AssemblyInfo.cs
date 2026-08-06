using System.Runtime.InteropServices;

// Nothing in this assembly is exposed to COM unless it says so. The four
// driver classes and the handful of helper types a telescope returns carry
// ComVisible(true) of their own, and everything else, the class factory
// plumbing included, stays out of reach of COM clients.
//
// This lives in a file rather than in the project's ComVisible property
// because SDK style projects ignore that property: it never reaches the
// generated assembly attributes, so setting it there would look like a
// decision while doing nothing at all.
[assembly: ComVisible(false)]
