// ScriptCompiler.cs
// Script#/Core/Compiler
// This source code is subject to terms and conditions of the Apache License, Version 2.0.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using ScriptSharp.CodeModel;
using ScriptSharp.Compiler;
using ScriptSharp.Generator;
using ScriptSharp.Importer;
using ScriptSharp.ResourceModel;
using ScriptSharp.ScriptModel;
using ScriptSharp.Validator;

namespace ScriptSharp {

    /// <summary>
    /// The Script# compiler.
    /// </summary>
    public sealed class ScriptCompiler : IErrorHandler {

        private CompilerOptions _options;
        private IErrorHandler _errorHandler;

        private ParseNodeList _compilationUnitList;
        private SymbolSet _symbols;
        private ICollection<TypeSymbol> _importedSymbols;
        private ICollection<TypeSymbol> _appSymbols;
        private bool _hasErrors;

#if DEBUG
        private string _testOutput;
#endif

        public ScriptCompiler()
            : this(null) {
        }

        public ScriptCompiler(IErrorHandler errorHandler) {
            _errorHandler = errorHandler;
        }

        private void BuildCodeModel() {
            _compilationUnitList = new ParseNodeList();

            CodeModelBuilder codeModelBuilder = new CodeModelBuilder(_options, this);
            CodeModelValidator codeModelValidator = new CodeModelValidator(this);
            CodeModelProcessor validationProcessor = new CodeModelProcessor(codeModelValidator, _options);

            foreach (IStreamSource source in _options.Sources) {
                CompilationUnitNode compilationUnit = codeModelBuilder.BuildCodeModel(source);
                if (compilationUnit != null) {
                    validationProcessor.Process(compilationUnit);

                    _compilationUnitList.Append(compilationUnit);
                }
            }
        }

        private void BuildImplementation() {
            CodeBuilder codeBuilder = new CodeBuilder(_options, this);
            ICollection<SymbolImplementation> implementations = codeBuilder.BuildCode(_symbols);

            if (_options.Minimize) {
                foreach (SymbolImplementation impl in implementations) {
                    if (impl.Scope == null) {
                        continue;
                    }

                    SymbolObfuscator obfuscator = new SymbolObfuscator();
                    SymbolImplementationTransformer transformer = new SymbolImplementationTransformer(obfuscator);

                    transformer.TransformSymbolImplementation(impl);
                }
            }
        }

        private void BuildMetadata() {
            if ((_options.Resources != null) && (_options.Resources.Count != 0)) {
                ResourcesBuilder resourcesBuilder = new ResourcesBuilder(_symbols);
                resourcesBuilder.BuildResources(_options.Resources);
            }

            MetadataBuilder mdBuilder = new MetadataBuilder(this);
            _appSymbols = mdBuilder.BuildMetadata(_compilationUnitList, _symbols, _options);

            // Check if any of the types defined in this assembly conflict.
            Dictionary<string, TypeSymbol> types = new Dictionary<string, TypeSymbol>();
            foreach (TypeSymbol appType in _appSymbols) {
                if ((appType.IsApplicationType == false) || (appType.Type == SymbolType.Delegate)) {
                    // Skip the check for types that are marked as imported, as they
                    // aren't going to be generated into the script.
                    // Delegates are implicitly imported types, as they're never generated into
                    // the script.
                    continue;
                }

                if ((appType.Type == SymbolType.Class) &&
                    (((ClassSymbol)appType).PrimaryPartialClass != appType)) {
                    // Skip the check for partial types, since they should only be
                    // checked once.
                    continue;
                }

                string name = appType.GeneratedName;
                if (types.ContainsKey(name)) {
                    string error = "The type '" + appType.FullName + "' conflicts with with '" + types[name].FullName + "' as they have the same name.";
                    ((IErrorHandler)this).ReportError(error, null);
                }
                else {
                    types[name] = appType;
                }
            }

            // Capture whether there are any test types in the project
            // when not compiling the test flavor script. This is used to determine
            // if the test flavor script should be compiled in the build task.

            if (_options.IncludeTests == false) {
                foreach (TypeSymbol appType in _appSymbols) {
                    if (appType.IsApplicationType && appType.IsTestType) {
                        _options.HasTestTypes = true;
                    }
                }
            }

#if DEBUG
            if (_options.InternalTestType == "metadata") {
                StringWriter testWriter = new StringWriter();

                testWriter.WriteLine("Metadata");
                testWriter.WriteLine("================================================================");

                SymbolSetDumper symbolDumper = new SymbolSetDumper(testWriter);
                symbolDumper.DumpSymbols(_symbols);

                testWriter.WriteLine();
                testWriter.WriteLine();

                _testOutput = testWriter.ToString();
            }
#endif // DEBUG

            ISymbolTransformer transformer = null;
            if (_options.Minimize) {
                transformer = new SymbolObfuscator();
            }
            else {
                transformer = new SymbolInternalizer();
            }

            if (transformer != null) {
                SymbolSetTransformer symbolSetTransformer = new SymbolSetTransformer(transformer);
                ICollection<Symbol> transformedSymbols = symbolSetTransformer.TransformSymbolSet(_symbols, /* useInheritanceOrder */ true);

#if DEBUG
                if (_options.InternalTestType == "minimizationMap") {
                    StringWriter testWriter = new StringWriter();

                    testWriter.WriteLine("Minimization Map");
                    testWriter.WriteLine("================================================================");

                    List<Symbol> sortedTransformedSymbols = new List<Symbol>(transformedSymbols);
                    sortedTransformedSymbols.Sort(delegate(Symbol s1, Symbol s2) {
                        return String.Compare(s1.Name, s2.Name);
                    });

                    foreach (Symbol transformedSymbol in sortedTransformedSymbols) {
                        Debug.Assert(transformedSymbol is MemberSymbol);
                        testWriter.WriteLine("    Member '" + transformedSymbol.Name + "' renamed to '" + transformedSymbol.GeneratedName + "'");
                    }

                    testWriter.WriteLine();
                    testWriter.WriteLine();

                    _testOutput = testWriter.ToString();
                }
#endif // DEBUG
            }
        }

        public bool Compile(CompilerOptions options) {
            if (options == null) {
                throw new ArgumentNullException("options");
            }
            _options = options;

            _hasErrors = false;
            _symbols = new SymbolSet();

            ImportMetadata();
            if (_hasErrors) {
                return false;
            }

            BuildCodeModel();
            if (_hasErrors) {
                return false;
            }

            BuildMetadata();
            if (_hasErrors) {
                return false;
            }

            BuildImplementation();
            if (_hasErrors) {
                return false;
            }

            GenerateScript();
            if (_hasErrors) {
                return false;
            }

            return true;
        }

        private void GenerateScript() {
            Stream outputStream = null;
            TextWriter outputWriter = null;

            try {
                outputStream = _options.ScriptFile.GetStream();
                if (outputStream == null) {
                    ((IErrorHandler)this).ReportError("Unable to write to file " + _options.ScriptFile.FullName,
                                                      _options.ScriptFile.FullName);
                    return;
                }

                outputWriter = new StreamWriter(outputStream);

#if DEBUG
                if (_options.InternalTestMode) {
                    if (_testOutput != null) {
                        outputWriter.Write(_testOutput);
                        outputWriter.WriteLine("Script");
                        outputWriter.WriteLine("================================================================");
                        outputWriter.WriteLine();
                        outputWriter.WriteLine();
                    }
                }
#endif // DEBUG

                string script = GenerateScriptWithTemplate();
                outputWriter.Write(script);
            }
            catch (Exception e) {
                Debug.Fail(e.ToString());
            }
            finally {
                if (outputWriter != null) {
                    outputWriter.Flush();
                }
                if (outputStream != null) {
                    _options.ScriptFile.CloseStream(outputStream);
                }
            }
        }

        private string GenerateScriptCore() {
            StringWriter scriptWriter = new StringWriter();

            try {
                ScriptGenerator scriptGenerator = new ScriptGenerator(scriptWriter, _options, _symbols);
                scriptGenerator.GenerateScript(_symbols);
            }
            catch (Exception e) {
                Debug.Fail(e.ToString());
            }
            finally {
                scriptWriter.Flush();
            }

            return scriptWriter.ToString();
        }

        private string GenerateScriptWithTemplate() {
            string script = GenerateScriptCore();
            if (String.IsNullOrEmpty(_options.Template)) {
                return script;
            }

            StringBuilder requiresBuilder = new StringBuilder();
            StringBuilder dependenciesBuilder = new StringBuilder();

            bool firstDependency = true;
            foreach (string dependency in _symbols.Dependencies) {
                if (firstDependency == false) {
                    requiresBuilder.Append(", ");
                    dependenciesBuilder.Append(", ");
                }

                requiresBuilder.Append("'" + dependency + "'");

                if (String.Compare(dependency, "jquery", StringComparison.OrdinalIgnoreCase) == 0) {
                    // TODO: This is a hack ... may want to generalize
                    //       to allow module name and associated variable
                    //       to differ.
                    dependenciesBuilder.Append("$");
                }
                else {
                    dependenciesBuilder.Append(dependency);
                }

                firstDependency = false;
            }

            return _options.Template.TrimStart()
                                    .Replace("{name}", _symbols.ScriptName)
                                    .Replace("{requires}", requiresBuilder.ToString())
                                    .Replace("{dependencies}", dependenciesBuilder.ToString())
                                    .Replace("{script}", script);
        }

        private void ImportMetadata() {
            MetadataImporter mdImporter = new MetadataImporter(_options, this);

            _importedSymbols = mdImporter.ImportMetadata(_options.References, _symbols);
        }

        #region Implementation of IErrorHandler
        void IErrorHandler.ReportError(string errorMessage, string location) {
            _hasErrors = true;

            if (_errorHandler != null) {
                _errorHandler.ReportError(errorMessage, location);
                return;
            }

            if (String.IsNullOrEmpty(location) == false) {
                Console.Error.Write(location);
                Console.Error.Write(": ");
            }

            Console.Error.WriteLine(errorMessage);
        }
        #endregion
    }
}
