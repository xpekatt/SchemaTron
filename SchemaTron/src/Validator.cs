﻿/*! \mainpage SchemaTron - API reference
 *
 * %SchemaTron represents a native C# implementation (.NET 4.0 DLL assembly)
 * of the ISO Schematron validation language over XPath 1.0 query language.
 * 
 * ISO Schematron is a relatively simple language based on XML capable of
 * specifying XML schemas. It allows to express a valid document directly
 * with a set of rules and assertions, specified using an external query
 * language. It is designed for a different style of validation than the
 * typical grammar-based schemas and it does not need the whole gramatical
 * infrastructure (DTD, XSD, Relax NG).
 * 
 * Please find more information in the full documentation which can be found
 * at the project home page: http://assembla.com/spaces/xrouter .
 */

using System;
using System.Collections.Generic;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using SchemaTron.Preprocessing;
using SchemaTron.SyntaxModel;
using XmlPrime;

namespace SchemaTron
{
    /// <summary>
    /// A native ISO Schematron validator using XPath 1.0 query language binding.
    /// </summary>
    /// <remarks>
    /// Validator instances can be created via Create() factory methods.
    /// Validation itself is done using the Validate() method.
    /// </remarks>
    /// <seealso cref="Create(XDocument)"/>
    /// <seealso cref="Create(XDocument, ValidatorSettings)"/>
    /// <seealso cref="Validate(XDocument, Boolean)"/>
    public sealed class Validator
    {
        /// <summary>
        /// Schematron schema for validation - in internal format.
        /// </summary>
        private Schema schema = null;

        /// <summary>
        /// Private constructor to force instance creation via factory methods.
        /// </summary>
        private Validator()
        {
        }

        /// <summary>
        /// Gets schema syntax in minimal form - in original XML format.
        /// </summary>
        public XDocument MinSyntax { get; private set; }

        /// <summary>
        /// Creates a new Validator instance given a Schematron syntax schema and custom
        /// validator settings.
        /// </summary>
        /// <param name="xSchema">ISO Schematron complex syntax schema.
        /// Must not be null.</param>
        /// <param name="settings">Validator settings</param>
        /// <returns>A new Validator instance</returns>     
        /// <exception cref="System.ArgumentException"/>
        /// <exception cref="System.ArgumentNullException"/>
        /// <exception cref="SyntaxException"/>
        public static Validator Create(XDocument xSchema, ValidatorSettings settings = null, Uri baseUri = null)
        {
            if (xSchema == null)
            {
                throw new ArgumentNullException("xSchema");
            }

            if (settings == null)
            {
                settings = new ValidatorSettings();
            }

            if (settings.InclusionsResolver == null)
            {
                settings.InclusionsResolver = new FileInclusionResolver();
            }
            
            if (xSchema.Root == null)
            {
                throw new ArgumentException("Schema must contain root node.");
            }

            Validator validator = null;

            // work-around for lack of support of xslt.current() within XmlPrime.XPath.Compile()
            XDocument schemaXmlWithCurrentWorkaround = XDocument.Parse(xSchema.ToString().Replace("current()", "$current"));

            string defaultNS = schemaXmlWithCurrentWorkaround.Root.GetDefaultNamespace().ToString();
            if (defaultNS == Constants.OneDotFiveNamespace)
            {
                throw new ApplicationException("Schematron 1.5 is not supported.");
            }
            else if (defaultNS != Constants.ISONamespace)
            {
                throw new ApplicationException("This is not an ISO Schematron file.");
            }
            
            // resolve ISO namespace
            XmlNamespaceManager nsManager = new XmlNamespaceManager(new NameTable());
            nsManager.AddNamespace("sch", Constants.ISONamespace);

            settings.Phase = DetermineSchemaPhase(schemaXmlWithCurrentWorkaround.Root, settings.Phase, nsManager);

            // preprocessing - turn to minimal form
            Preprocess(schemaXmlWithCurrentWorkaround, nsManager, settings);

            // deserialization                           
            Schema minimalizedSchema = SchemaDeserializer.Deserialize(schemaXmlWithCurrentWorkaround, nsManager);

            // xpath preprocessing
            CompileXPathExpressions(minimalizedSchema, baseUri);

            // create instance
            validator = new Validator();
            validator.schema = minimalizedSchema;
            validator.MinSyntax = xSchema;

            return validator;
        }

        /// <summary>
        /// Determines the name of the validation phase.
        /// </summary>
        /// <remarks>
        /// Symbolic phase name #ALL is left as is, #DEFAULT is tried to be
        /// resolved into a concrete phase name. Concrete phase names are
        /// searched for in the schema to find out if they really exist.
        /// </remarks>
        /// <param name="xRoot">Root of a XML schema where to search for a phase.
        /// Must not be null.</param>
        /// <param name="phase">Symbolic (#ALL, #DEFAULT) or concrete phase name.
        /// Must not be null.</param>
        /// <param name="nsManager">Namespace manager. Must not be null.</param>
        /// <returns>Symbolic phase name (#ALL) or a phase name of a concrete
        /// existing phase</returns>
        /// <exception cref="System.ArgumentException">if no default phase name
        /// is specified in the schema or a concrete phase does not exist in the
        /// schema</exception>
        private static string DetermineSchemaPhase(XElement xRoot, string phase, XmlNamespaceManager nsManager)
        {
            if (xRoot == null)
            {
                throw new ArgumentNullException("xRoot - XML schema root element");
            }
            if (phase == null)
            {
                throw new ArgumentNullException("phase");
            }
            else if (phase == "#ALL")
            {
                return phase;
            }
            else if (phase == "#DEFAULT")
            {
                XAttribute xPhase = xRoot.Attribute(XName.Get("defaultPhase"));
                if (xPhase != null)
                {
                    return xPhase.Value;
                }
                else
                {
                    throw new ArgumentException("schema.@defaultPhase is not specified.", "phase");
                }
            }
            else if (xRoot.XPathSelectElement(String.Format("/sch:schema/sch:phase[@id='{0}']", phase), nsManager) != null)
            {
                return phase;
            }
            else
            {
                throw new ArgumentException(String.Format("Phase[@id='{0}'] is not specified.", phase), "Phase");
            }
        }

        /// <summary>
        /// Preprocesses the XML schema (in place).
        /// </summary>
        /// <remarks>
        /// The preprocessing can be turned off in the validator settings.
        /// </remarks>
        /// <param name="xSchema">Schema</param>
        /// <param name="nsManager">Namespace manager</param>
        /// <param name="settings">Validator settings</param>
        /// <exception cref="SyntaxException">If any of the intermediate
        /// results is not valid.</exception>
        private static void Preprocess(XDocument xSchema, XmlNamespaceManager nsManager, ValidatorSettings settings)
        {
            if (!settings.Preprocessing)
            {
                return;
            }

            ValidatorSettings valArgs = new ValidatorSettings();
            valArgs.Preprocessing = false;

            // validation - phaseA
            XDocument xPhaseA = Resources.Provider.SchemaPhaseA;
            Validator validatorPhaseA = Validator.Create(xPhaseA, valArgs);

            // workaround bug in xmlprime which assumes NameTable is pre-populated within XDocument.CreateNavigator()
            XPathNavigator schemaNavigator = new XdmDocument(xSchema.CreateReader()).CreateNavigator();

            ValidatorResults resultsA = validatorPhaseA.Validate(schemaNavigator, true);
            if (!resultsA.IsValid)
            {
                throw new SyntaxException(resultsA.GetMessages());
            }

            Preprocessor.ResolveInclusions(xSchema, settings.InclusionsResolver, nsManager);

            // validation - phaseB
            XDocument xPhaseB = Resources.Provider.SchemaPhaseB;
            Validator validatorPhaseB = Validator.Create(xPhaseB, valArgs);

            // ResolveInclusions could have modified xSchema -- update XdmDocument obj 
            schemaNavigator = new XdmDocument(xSchema.CreateReader()).CreateNavigator();

            ValidatorResults resultsB = validatorPhaseB.Validate(schemaNavigator, true);
            if (!resultsB.IsValid)
            {
                throw new SyntaxException(resultsB.GetMessages());
            }

            Preprocessor.ResolveAbstractPatterns(xSchema, nsManager);
            Preprocessor.ResolveAbstractRules(xSchema, nsManager);
            Preprocessor.ResolvePhase(xSchema, nsManager, settings.Phase);
            Preprocessor.ResolveDiagnostics(xSchema, nsManager);

            // validation - phaseC 
            XDocument xPhaseC = Resources.Provider.SchemaPhaseC;
            Validator validatorPhaseC = Validator.Create(xPhaseC, valArgs);

            // last preprocessor stpes could have modified xSchema -- update XdmDocument obj 
            schemaNavigator = new XdmDocument(xSchema.CreateReader()).CreateNavigator();

            ValidatorResults resultsC = validatorPhaseC.Validate(schemaNavigator, true);
            if (!resultsC.IsValid)
            {
                throw new SyntaxException(resultsC.GetMessages());
            }

            Preprocessor.ResolveLets(xSchema, nsManager);
            Preprocessor.ResolveAncillaryElements(xSchema, nsManager);
        }

        /// <summary>
        /// Compiles XPath expressions in schema patterns.
        /// </summary>
        /// <remarks>
        /// The XPath expression are compiled in place inside the schema.
        /// </remarks>
        /// <param name="schema">Schema which contains the expressions to be
        /// compiled</param>
        /// <exception cref="SyntaxException">Thrown if any of the expressions
        /// does not conform to XPath 1.0. The exceptions may contain multiple
        /// error messages.</exception>
        private static void CompileXPathExpressions(Schema schema, Uri baseUri)
        {
            List<string> messages = new List<string>();

            // resolve namespaces
            NameTable nameTable = new NameTable();
            XmlNamespaceManager nsManager = new XmlNamespaceManager(nameTable);
            foreach (Namespace ns in schema.Namespaces)
            {
                nsManager.AddNamespace(ns.Prefix, ns.Uri);
            }
            XPathSettings compilationSettings = new XPathSettings(nameTable, nsManager);
            compilationSettings.ImportModule(XdmModule.XsltFunctions);
            compilationSettings.ImportModule(XdmModule.MsXslFunctions);
            compilationSettings.ImportModule(XdmModule.ExtensionFunctions);
            compilationSettings.ImportModule(XdmModule.XQuery30Functions);
            compilationSettings.ImportModule(XdmModule.XPathMathFunctions);

            if (baseUri != null)
            {
                compilationSettings.BaseURI = new AnyUri(baseUri);
            }

            // work-around for lack of support of xslt.current() within XmlPrime.XPath.Compile()
            compilationSettings.ImplicitVariableDeclaration = true;

            // compile XPath expressions
            foreach (Pattern pattern in schema.Patterns)
            {
                // compile contexts
                foreach (Rule rule in pattern.Rules)
                {
                    // alter xpath context
                    string context = rule.Context;
                    if (context.Length > 0 && context[0] != '/')
                    {
                        context = String.Concat("//", context);
                    }

                    try
                    {
                        rule.CompiledContext = XPath.Compile(context, compilationSettings);
                    }
                    catch (XdmException e)
                    {
                        messages.Add(String.Format("Invalid XPath 2.0 context='{0}': {1}", rule.Context, e.Message));                        
                    }
                    catch (XPathException e)
                    {
                        messages.Add(String.Format("Invalid XPath 2.0 context='{0}': {1}", rule.Context, e.Message));
                    }

                    // compile tests
                    foreach (Assert assert in rule.Asserts)
                    {
                        try
                        {
                            assert.CompiledTest = XPath.Compile(assert.Test, compilationSettings);
                        }
                        catch (XdmException e)
                        {
                            messages.Add(String.Format("Invalid XPath 2.0 test='{0}': {1}", assert.Test, e.Message));
                        }
                        catch (XPathException e)
                        {
                            messages.Add(String.Format("Invalid XPath 2.0 test='{0}': {1}", assert.Test, e.Message));
                        }

                        // compile diagnostics
                        if (assert.Diagnostics.Length > 0)
                        {
                            assert.CompiledDiagnostics = new XPath[assert.Diagnostics.Length];
                            for (int i = 0; i < assert.Diagnostics.Length; i++)
                            {
                                string diag = assert.Diagnostics[i];
                                try
                                {
                                    assert.CompiledDiagnostics[i] = XPath.Compile(diag, compilationSettings);
                                }
                                catch (XdmException e)
                                {
                                    if (assert.DiagnosticsIsValueOf[i])
                                    {
                                        messages.Add(String.Format("Invalid XPath 2.0 select='{0}': {1}", diag, e.Message));
                                    }
                                    else
                                    {
                                        messages.Add(String.Format("Invalid XPath 2.0 path='{0}']: {1}", diag, e.Message));
                                    }
                                }
                                catch (XPathException e)
                                {
                                    if (assert.DiagnosticsIsValueOf[i])
                                    {
                                        messages.Add(String.Format("Invalid XPath 2.0 select='{0}': {1}", diag, e.Message));
                                    }
                                    else
                                    {
                                        messages.Add(String.Format("Invalid XPath 2.0 path='{0}']: {1}", diag, e.Message));
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // syntax errors
            if (messages.Count > 0)
            {
                throw new SyntaxException(messages);
            }
        }

        /// <summary>
        /// Validates an XML document with the current validator settings.
        /// The process can stop at the first assertion or validate the whole
        /// document.
        /// </summary>
        /// <remarks>This method is NOT thread-safe.</remarks>
        /// <param name="xDocument">An instance of an XML document to be validated.
        /// Must not be null.
        /// It is recommended to supply a document with line information
        /// for better diagnostics.</param>
        /// <param name="fullValidation">Indicates whether to validate the
        /// whole document regardless of any assertion, or to stop validation at
        /// the first assertion.
        /// </param>
        /// <returns>Detailed validation results.</returns>
        /// <exception cref="ArgumentNullException">If xDocument is null.</exception>
        public ValidatorResults Validate(XPathNavigator navigator, bool fullValidation)
        {
            if (navigator == null)
            {
                throw new ArgumentNullException("navigator");
            }
            ValidationEvaluator evaluator = new ValidationEvaluator(this.schema, navigator, fullValidation);
            return evaluator.Evaluate();
        }

        public ValidatorResults Validate(XDocument aDoc, bool fullValidation)
        {
            if (aDoc == null)
            {
                throw new ArgumentNullException("aDoc");
            }
            // workaround for XmlPrime 
            XdmDocument xmlprimeDoc = new XdmDocument(aDoc.CreateReader());
            ValidationEvaluator evaluator = new ValidationEvaluator(this.schema, xmlprimeDoc.CreateNavigator(), fullValidation);
            return evaluator.Evaluate();
        }
    }
}
