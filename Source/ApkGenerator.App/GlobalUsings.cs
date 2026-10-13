// The WPF SDK leaves these out of the implicit usings because they clash with System.Windows types
// (e.g. System.Windows.Shapes.Path); this project never imports System.Windows.Shapes, so it's safe to add them back.
global using System.IO;
global using System.Threading;
