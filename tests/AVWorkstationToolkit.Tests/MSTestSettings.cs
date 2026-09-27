// Tests that build WPF windows are marked DoNotParallelize: each runs on its own UI thread, and WPF reads XAML from the
// assembly's resource package, which isn't safe for two threads at once (it fails with a NullReferenceException in
// System.IO.Packaging.PackagePart).
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
