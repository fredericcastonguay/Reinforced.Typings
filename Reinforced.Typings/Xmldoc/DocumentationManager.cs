﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Reinforced.Typings.Generators;
using Reinforced.Typings.Xmldoc.Model;

namespace Reinforced.Typings.Xmldoc
{
    /// <summary>
    /// Generation documentation manager
    /// </summary>
    public class DocumentationManager
    {
        private bool _isDocumentationExists;
        private Documentation _documentation;
        private readonly Dictionary<Type, DocumentationMember> _docsForTypes = new Dictionary<Type, DocumentationMember>();
        private readonly Dictionary<ConstructorInfo, DocumentationMember> _docsForConstructors = new Dictionary<ConstructorInfo, DocumentationMember>();
        private readonly Dictionary<MemberInfo, DocumentationMember> _docsForMembers = new Dictionary<MemberInfo, DocumentationMember>();
        private const BindingFlags All = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private readonly ExportSettings _settings;
        internal DocumentationManager(string docFilePath, ExportSettings settings)
        {
            _settings = settings;
            CacheDocumentation(docFilePath);
        }

        private void CacheDocumentation(string docFilePath)
        {
            if (string.IsNullOrEmpty(docFilePath)) return;
            if (!File.Exists(docFilePath)) return;
            try
            {
                XmlSerializer ser = new XmlSerializer(typeof(Documentation));
                using (var fs = File.OpenRead(docFilePath))
                {
                    _documentation = (Documentation)ser.Deserialize(fs);
                }
                BuildDocumentationCache();
                _isDocumentationExists = true;
            }
            catch (Exception)
            {
                _isDocumentationExists = false;
            }
        }

        private void Store<T>(Dictionary<T, DocumentationMember> d, T member, DocumentationMember doc) where T : class
        {
            if (member == null) return;
            d[member] = doc;
        }
        private void BuildDocumentationCache()
        {
            foreach (var dm in _documentation.Members)
            {
                Type type;
                MethodBase method;
                string before, after, parameters;

                switch (dm.MemberType)
                {
                    case DocumentationMemberType.Type:
                        type = _settings.GetSourceAssemblyType(dm.Name);
                        if (type != null) Store(_docsForTypes, type, dm);
                        break;
                    case DocumentationMemberType.Field:
                    case DocumentationMemberType.Property:

                        GetLastEntityName(dm.Name, out before, out after);
                        type = _settings.GetSourceAssemblyType(before);
                        var member = type.GetMember(after, All)[0];
                        Store(_docsForMembers, member, dm);
                        break;
                    case DocumentationMemberType.Method:
                        if (!dm.Name.Contains('('))
                        {
                            GetLastEntityName(dm.Name, out before, out after);
                            type = _settings.GetSourceAssemblyType(before);
                            method = type.GetMethod(RemoveGenericQuotes(after));
                            Store(_docsForMembers, method, dm);
                            break;
                        }
                        var idx = dm.Name.IndexOf('(');
                        var typeAndMethod = dm.Name.Substring(0, idx);
                        parameters = dm.Name.Substring(idx).Trim('(').Trim(')');
                        GetLastEntityName(typeAndMethod, out before, out after);
                        type = _settings.GetSourceAssemblyType(before);
                        method = GetMethodWithParams(type, after, parameters);
                        Store(_docsForMembers, method, dm);
                        break;
                    case DocumentationMemberType.Constructor:
                        var nameParts = dm.Name.Split(new[] { ".#ctor" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var namePart in nameParts)
                        {
                            Console.WriteLine("Namepart: {0}", namePart);
                        }
                        type = _settings.GetSourceAssemblyType(nameParts[0]);
                        parameters = nameParts[1].Trim('(').Trim(')');
                        method = GetMethodWithParams(type, "#ctor", parameters);
                        Store(_docsForConstructors, (ConstructorInfo)method, dm);
                        break;
                }
            }
        }

        private void GetLastEntityName(string fullName, out string before, out string after)
        {
            var dotIndex = fullName.LastIndexOf('.');
            before = fullName.Substring(0, dotIndex);
            after = fullName.Substring(dotIndex + 1);
            Console.WriteLine("before {0}", before);
            Console.WriteLine("after {0}", after);
        }

        private string RemoveGenericQuotes(string input)
        {
            if (!input.Contains('`')) return input;
            var idx = input.IndexOf('`');
            return input.Substring(0, idx);
        }
        private readonly Dictionary<string, MethodBase> _methodsCache = new Dictionary<string, MethodBase>();

        private MethodBase GetMethodWithParams(Type type, string name, string parametersTypesString)
        {
            var key = name + parametersTypesString;
            if (_methodsCache.ContainsKey(key)) return _methodsCache[key]; //make it a little bit faster

            // lets try simple
            MethodBase[] methods;
            if (name == "#ctor") methods = type.GetConstructors(All).Cast<MethodBase>().ToArray();
            else methods = type.GetMethods(All).Where(c => c.Name == RemoveGenericQuotes(name)).Cast<MethodBase>().ToArray();

            if (!methods.Any()) return null;
            var single = methods.SingleOrDefault();
            if (single != null)
            {
                _methodsCache.Add(key, single);
                return single;
            }

            // okay then
            // well.. lets go complicated way then
            var typeGenericArgs = type.GetGenericArguments();
            var typeGenericsDict = typeGenericArgs
                .Select((a, i) => new { a, i })
                .ToDictionary(c => c.a, c => c.i); // type -> `0, type -> `1
            foreach (var methodBase in methods)
            {
                var methodGenericArgs = methodBase.GetGenericArguments();
                var methodGenericArgsDict = methodGenericArgs
                    .Select((a, i) => new { a, i })
                    .ToDictionary(c => c.a, c => c.i); //type -> ``0, type -> ``1
                List<string> names = new List<string>();
                foreach (var param in methodBase.GetParameters())
                {
                    var friendlyName = GetDocFriendlyParameterName(param.ParameterType, typeGenericsDict,
                        methodGenericArgsDict);
                    if (param.IsOut || param.ParameterType.IsByRef) friendlyName = friendlyName + "@";
                    names.Add(friendlyName);
                }
                var generatedTypes = String.Join(",", names);

                _methodsCache[name + generatedTypes] = methodBase;

                if (generatedTypes == parametersTypesString)
                {
                    return methodBase;
                }
            }
            return null;
        }

        private static string GetDocFriendlyParameterName(Type parameterType,
            Dictionary<Type, int> typeGenericsDict,
            Dictionary<Type, int> methodGenericArgsDict)
        {
            if (typeGenericsDict.ContainsKey(parameterType))
            {
                return ("`" + typeGenericsDict[parameterType]);
            }
            if (methodGenericArgsDict.ContainsKey(parameterType))
            {
                return ("``" + methodGenericArgsDict[parameterType]);
            }

            if (parameterType.IsGenericType)
            {
                var gen = parameterType.GetGenericTypeDefinition();
                var name = gen.FullName;
                var quote = name.IndexOf('`');
                name = name.Substring(0, quote);
                var genericParams = parameterType.GetGenericArguments()
                    .Select(c => GetDocFriendlyParameterName(c, typeGenericsDict, methodGenericArgsDict)).ToArray();
                name = String.Format("{0}{{{1}}}", name, string.Join(",", genericParams));
                return name;

            }
            return parameterType.FullName.Trim('&');
        }

        /// <summary>
        /// Outputs documentation for class member
        /// </summary>
        /// <param name="member">Class member</param>
        /// <param name="sw">Text writer</param>
        public void WriteDocumentation(MemberInfo member, WriterWrapper sw)
        {
            if (member == null) return;
            if (!_isDocumentationExists) return;
            if (!_docsForMembers.ContainsKey(member)) return;
            var info = member as MethodInfo;
            if (info != null)
            {
                WriteDocumentation(info, sw);
                return;
            }
            var doc = _docsForMembers[member];
            if (!doc.HasSummary()) return;
            Begin(sw);
            Summary(sw, doc.Summary.Text);
            End(sw);
        }

        /// <summary>
        /// Outputs documentation for method
        /// </summary>
        /// <param name="method">Method</param>
        /// <param name="sw">Text writer</param>
        public void WriteDocumentation(MethodInfo method, WriterWrapper sw)
        {
            if (method == null) return;
            if (!_isDocumentationExists) return;
            if (!_docsForMembers.ContainsKey(method)) return;
            var doc = _docsForMembers[method];
            if ((!doc.HasSummary()) && (!doc.HasParameters()) && (!doc.HasReturns())) return;

            Begin(sw);
            if (doc.HasSummary()) Summary(sw, doc.Summary.Text);
            if (doc.HasParameters())
            {
                if (doc.HasSummary()) Line(sw);
                WriteParametersDoc(method.GetParameters(), doc, sw);
            }
            if (doc.HasReturns())
            {
                if (doc.HasSummary() || doc.HasParameters()) Line(sw);
                Line(sw, string.Format("@returns {0}", doc.Returns.Text));
            }
            End(sw);

        }

        /// <summary>
        /// Outputs documentation for constructor
        /// </summary>
        /// <param name="constructor">Constructor</param>
        /// <param name="sw">Text writer</param>
        public void WriteDocumentation(ConstructorInfo constructor, WriterWrapper sw)
        {
            if (constructor == null) return;
            if (!_isDocumentationExists) return;
            if (!_docsForConstructors.ContainsKey(constructor)) return;
            var doc = _docsForConstructors[constructor];
            if ((!doc.HasSummary()) && (!doc.HasParameters())) return;

            Begin(sw);
            if (doc.HasSummary())
            {
                Summary(sw, doc.Summary.Text);
                Line(sw);
            }
            Line(sw, "@constructor");
            if (doc.HasParameters())
            {
                
                WriteParametersDoc(constructor.GetParameters(), doc, sw);
            }
            End(sw);

        }

        /// <summary>
        /// Outputs documentation for type
        /// </summary>
        /// <param name="type">Type</param>
        /// <param name="sw">Text writer</param>
        public void WriteDocumentation(Type type, WriterWrapper sw)
        {
            if (type == null) return;
            if (!_isDocumentationExists) return;
            if (!_docsForTypes.ContainsKey(type)) return;
            var typeDoc = _docsForTypes[type];
            if (!typeDoc.HasSummary()) return;

            Begin(sw);
            Summary(sw, typeDoc.Summary.Text);
            End(sw);
        }

        private void WriteParametersDoc(ParameterInfo[] parameters, DocumentationMember docMember, WriterWrapper sw)
        {
            foreach (var parameterInfo in parameters)
            {
                var doc = docMember.Parameters.SingleOrDefault(c => c.Name == parameterInfo.Name);
                if (doc == null) continue;
                var name = parameterInfo.GetName();
                Line(sw, String.Format("@param {0} {1}", name, doc.Description));
            }
        }

        private void Begin(WriterWrapper sw)
        {
            sw.WriteLine("/**");
        }

        private void Summary(WriterWrapper sw, string summary)
        {
            if (string.IsNullOrEmpty(summary)) return;
            var summaryLines = summary.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var summaryLine in summaryLines)
            {
                Line(sw, summaryLine);
            }
        }

        private void Line(WriterWrapper sw, string line = null)
        {
            if (string.IsNullOrEmpty(line)) sw.WriteLine("*");
            else sw.WriteLine("* {0}", line);
        }

        /// <summary>
        /// Writes output comment with automatic multiline division
        /// </summary>
        /// <param name="sw">Output writer</param>
        /// <param name="comment">Comment (multiline allowed)</param>
        public void WriteComment(WriterWrapper sw, string comment)
        {
            sw.Br();
            sw.Indent();
            var lines = comment.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 1)
            {
                sw.WriteLine("// {0} ", lines[0]);
            }
            else
            {
                Begin(sw);
                foreach (var line in lines)
                {
                    Line(sw,line);
                }
                End(sw);
                sw.Br();
            }

        }
        
        private void End(WriterWrapper sw)
        {
            sw.WriteLine("*/");
        }


    }
}
