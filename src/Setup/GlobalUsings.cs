// Global usings for the whole project.
//
// These mirror the SDK's ImplicitUsings set, but are declared as real source so
// they are also picked up by the WPF markup-compilation temp project
// (Setup_*_wpftmp.csproj), which in some SDK builds does not inherit the
// implicit `System.IO` / `System.Net.Http` usings and otherwise fails to find
// Path/Directory/File/FileInfo/HttpClient during XAML compilation.
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
