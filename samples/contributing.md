Contributing to F# Data
=======================

This page should provide you with some basic information if you're thinking about
contributing to the F# Data package. It gives a brief summary of the library 
structure, how type providers are written and how the F# Data library handles 
multi-targeting (to make the providers available for Desktop, Silverlight as well
as Portable libraries).

 * This page can be edited by sending a pull request to F# Data on GitHub, so
   if you learn something when playing with F# Data, please record your
   [findings here](https://github.com/fsharp/FSharp.Data/blob/master/samples/contributing.md)!

 * If you want to discuss a feature (a good idea!), or if you want to look at 
   suggestions how you might contribute, check out the
   [Issue list](https://github.com/fsharp/FSharp.Data/issues) on GitHub or send
   an email to the [F# Open-Source mailing list](http://groups.google.com/group/fsharp-opensource).

## Solution files

The root directory contains a number of Visual Studio solutions (`*.sln`) files 
that group the projects in the main logical groups:

 * **FSharp.Data.sln** contains the main projects that implement most of the F# Data
   functionality (such as runtime and design-time type provider libraries). If you want
   to contribute code that is not quite ready yet, but looks interesting, then please
   add it to the experimental projects.

 * **FSharp.Data.Tests.sln** is a library with tests for F# Data and it also contains
   the content of this web site (as `*.fsx` and `*.md`) files. Look here if you want
   to edit the documentation!

 * **FSharp.Data.Demos.sln** contains a couple of larger projects that demonstrate the
   F# Data type providers in various configurations (Silverlight, Portable library
   referenced from Windows Phone applications). Feel free to contribute more exciting
   samples!

## Projects and multi-targetting

One problem with developing type providers is supporting multiple versions of the .NET 
platform. Type providers consist of two components:

 * **Runtime** is a part of the type provider that is actually used when the
   compiled F# code that uses the provider runs. For example, for CSV type provider,
   this contains the CSV parser and objects to load CSV files.

 * **Design time** is a part that is used when editing F# code that uses type
   provider in your favourite editor or when compiling code. For example, in the
   CSV provider, this component does the type inference and generates types
   (that are mapped to runtime components by the compiler).

To support multiple targets, we need a _runtime component_ for every single target
(Silverlight, .NET 4.0 and Portable profile). However, we only need one _design time_
component, because that is always going to be executed on desktop .NET in Visual Studio
or MonoDevelop. (Well, the truth is that we actually need another _design time_ version
for Silverlight to support the [tryfsharp.org](http://tryfsharp.org) web site...)

So, there are 3 versions of _runtime_ components and 2 versions of _design time_ 
components. At the moment, this is done by having separate project file for each
component, but they share the same files - the project just defines some symbols that
are then used to include/exclude parts that are not available on certain platforms
using `#if`. There are also 2 versions of _runtime_ components and 2 versions of _design time_ components
for the experimental projects.

If you open `FSharp.Data.sln`, you'll see the following projects for _runtime components_:

 * **FSharp.Data** - the desktop .NET 4.0 version
 * **FSharp.Data.Portable** - F# portable library version
 * **FSharp.Data.Silverlight** - a separate Silverlight version
 * **FSharp.Data.Experimental** - the desktop .NET 4.0 version of the experimental features
 * **FSharp.Data.Experimental.Portable** - F# portable library version of the experimental features

Although you could use the portable library in Silverlight, the Freebase provider doesn't work
correctly, so we have a separate Silverlight build. For the experimental project there's no
need to have a separate Silverlight build. The _design time_ components are in the following
projects:

 * **FSharp.Data.DesignTime** - the main version for desktop editors
 * **FSharp.Data.DesignTime.Silverlight** - an experimental version for Try F#
 * **FSharp.Data.Experimental.DesignTime** - the main version for desktop editors
 * **FSharp.Data.Experimental.DesignTime.Silverlight** - an experimental version for Try F#

### Type provider structure

Several of the F# Data type providers have similar structure - the CSV, JSON and XML
providers all infer the types from structure of a sample input. In addition, they all
have a runtime component (CSV parser, JSON parser or wrapper for `XDocument` type in .NET).

So, how is a typical type provider implemented? First of all, there are some shared 
files - in `Common` and `Library` subdirectories of the projects. These contain common
_runtime_ components (such as parsers, HTTP helpers, etc.)

Next, there are some common _design-time_ components. These can be found in `Providers`
folder (in the 2 design time projects) and contain the `ProvidedTypes` helpers from the
F# team, `StructureInference.fs` (which implements type inference for structured data)
and a couple of other helpers.

A type provider, such as JSON provider, is then located in a single folder with a number
of files, typically like this:

 * `JsonRuntime.fs` - the only _runtime_ component. Contains CSV parser and other 
   objects that are called by code generated by the type provider.

 * `JsonInference.fs` - _design-time_ component that infers the structure using 
   the common API in `StructureInference.fs`.

 * `JsonGenerator.fs` - implements code that generates provided types, adds properties
   and methods etc. This uses the information infered by inference and it generates
   calls to the runtime components.

 * `JsonProvider.fs` - entry point that defines static properties of the type provider,
   registers the provided types etc.

The WorldBank, Freebase and Apiary providers are different. They do not need inference, but 
they still distinguish between _runtime_ and _design-time_ components, so you'll find at least
two files (and possibly some additional helpers).

## Source code

### Assembly replacer

Generating code in type providers (see e.g. `JsonGenerator.fs`) is a bit tricky, because 
the generated code needs to contain references to the appropriate runtime assembly 
(Silverlight, Desktop or Portable profile). This is particularly tricky When using F# quotations 
to produce the generated code. If the source code contains `<@@ foo.Bar @@>`, then the 
quoation has a direct reference to the type of `foo` from the current assembly.

This is handled by Assembly replacer (see `AssemblyReplacer.fs` in `Providers`) which
transforms quotations and replaces references with the right versions. For more information
about how this works, see also the [discussion on GitHub](https://github.com/fsharp/FSharp.Data/pull/5).

Here is a documentation for the `AssemblyReplacer` type:

> When we split a type provider into a runtime assembly and a design time assembly, we can no longer 
> use quotations directly, because they will reference the wrong types. `AssemblyReplacer` fixes that 
> by transforming the expressions generated by the quotations to have the right types. 
>
> On all expressions  that we provide to `InvokeCode` and `GetterCode` of `ProvidedMethod`, `ProvidedConstructor`,
> and `ProvidedProperty`, instead of `(fun args -> <@@ doSomethingWith(%%args) @@>)`, we should use 
> `(fun args -> let args = replacer.ToDesignTime args in replacer.ToRuntime <@@ doSomethingWith(%%args) @@>)`.
>
> When creating the `ProvidedXYZ` type, we have to always specify the runtime type, and when it invokes
> the function provided to `InvokeCode` and `GetterCode`, we to first transform the argument expressions
> to the design time types, so we can splice it in the quotation, and then after that we have to convert
> it back to the runtime type. 
>
> A further complication arises because `Expr.Var`'s have reference equality, so when can't just create 
> new `Expr.Var`'s with the same variable name and a different type. When transforming them from runtime 
> to design time we keep them in a dictionary, so that when we convert them back to runtime we can return 
> the exact same instance that was provided to us initially.
>
> Another limitation (not only of this method, but in general with type providers) is that we can never use 
> expressions that use F# functions as parameters or return values, we always have to use felegates instead.

    [hide]
    open System
    open System.Reflection
    open Microsoft.FSharp.Quotations

All standard type providers obtain an instance of `AssemblyReplacer` when constructed and then pass it
to the code generator, which can use it to generate appropriate code:

    type AssemblyReplacer =
    
      /// Gets the equivalent runtime type
      abstract member ToRuntime : designTimeType:Type -> Type
      
      /// Gets an equivalent expression with all the types 
      /// replaced with runtime equivalents
      abstract member ToRuntime : designTimeTypeExpr:Expr -> Expr

      /// Gets an equivalent expression with all the types 
      /// replaced with designTime equivalents
      abstract member ToDesignTime: runtimeExpr:Expr -> Expr

## Documentation

The documentation for the F# Data library is automatically generated using the 
[F# Formatting](https://github.com/tpetricek/FSharp.Formatting) library. It turns 
`*.md` (Markdown with embedded code snippets) and `*.fsx` files (F# script file with 
embedded Markdown documentation) to a nice HTML documentation.

 * The code for all the documents can be found in the `samples` directory
   [on GitHub](https://github.com/fsharp/FSharp.Data/tree/master/samples). If you 
   find a bug or add a new feature, make sure you document it!

 * Aside from direct documentation for individual types, there is also a `tutorials` folder
   ([on GitHub](https://github.com/fsharp/FSharp.Data/tree/master/samples/tutorials)) where
   you can add additional samples and tutorials that show some interesting aspects of F# Data.

 * If you want to build the documentation, simply run the `build.fsx` script
   ([GitHub link](https://github.com/fsharp/FSharp.Data/blob/master/tools/build.fsx)) which
   builds the documentation.

## Related articles

If you want to learn more about writing type providers in general, here are some useful resources:

  * [Writing F# Type Providers with the F# 3.0 Developer Preview - An Introductory Guide and Samples](http://blogs.msdn.com/b/fsharpteam/archive/2011/09/24/developing-f-type-providers-with-the-f-3-0-developer-preview-an-introductory-guide-and-samples.aspx)

  * [F# 3.0 Sample Pack](http://fsharp3sample.codeplex.com/) contains a number of examples ranging
    from quite simple, to very complex.

  * [Tutorial: Creating a Type Provider (F#)](http://msdn.microsoft.com/en-gb/library/hh361034.aspx)
