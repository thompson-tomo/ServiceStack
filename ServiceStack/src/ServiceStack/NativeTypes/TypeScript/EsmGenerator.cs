using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ServiceStack.Text;
using ServiceStack.Web;

namespace ServiceStack.NativeTypes.TypeScript;

public class EsmGenerator : ILangGenerator
{
    /// <summary>
    /// Split assignment expression into smaller batches to avoid "Uncaught RangeError: Maximum call stack size exceeded" in Chrome/Blink
    /// </summary>
    public static int BatchSize { get; set; } = 100;
    
    public bool WithoutOptions { get; set; }
    public List<string> AddQueryParamOptions { get; set; }

    public readonly MetadataTypesConfig Config;
    readonly NativeTypesFeature feature;
    List<string> conflictTypeNames = new();
    public List<MetadataType> AllTypes { get; set; }

    public static Func<List<MetadataType>, List<MetadataType>> FilterTypes { get; set; } = DefaultFilterTypes;

    public static List<MetadataType> DefaultFilterTypes(List<MetadataType> types) => types.OrderTypesByDeps();
        
    public string DictionaryDeclaration { get; set; } = CreateEmptyClass("Dictionary");
        
    public HashSet<string> AddedDeclarations { get; set; } = new();

    /// <summary>
    /// Add Code to top of generated code
    /// </summary>
    public static AddCodeDelegate InsertCodeFilter { get; set; }

    /// <summary>
    /// Add Code to bottom of generated code
    /// </summary>
    public static AddCodeDelegate AddCodeFilter { get; set; }
    
    public TypeScriptGenerator Gen { get; set; }

    public Func<string, string> ReturnTypeFilter { get; set; } = type => type;

    public string Type(string type, string[] genericArgs)
    {
        var typeName = Gen.Type(type, genericArgs);
        typeName = typeName.LeftPart('<');
        return typeName;
    }

    public string DeclarationType(string type, string[] genericArgs, out string addDeclaration)
    {
        var typeName = Gen.DeclarationType(type, genericArgs, out addDeclaration);
        if (addDeclaration == Gen.DictionaryDeclaration)
            addDeclaration = DictionaryDeclaration;
        typeName = typeName.LeftPart('<');
        return typeName;
    }

    public EsmGenerator(MetadataTypesConfig config)
    {
        Config = config;
        feature = HostContext.GetPlugin<NativeTypesFeature>();
        Gen = new TypeScriptGenerator(config);
    }

    public string GetCode(MetadataTypes metadata, IRequest request, INativeTypesMetadata nativeTypes)
    {
        var typeNamespaces = new HashSet<string>();
        var includeList = metadata.RemoveIgnoredTypes(Config);
        metadata.Types.Each(x => typeNamespaces.Add(x.Namespace));
        metadata.Operations.Each(x => typeNamespaces.Add(x.Request.Namespace));

        var defaultImports = !Config.DefaultImports.IsEmpty()
            ? Config.DefaultImports
            : TypeScriptGenerator.DefaultImports;

        var globalNamespace = Config.GlobalNamespace;
        
        string defaultValue(string k) => request.QueryString[k].IsNullOrEmpty() ? "//" : "";

        var sbInner = StringBuilderCache.Allocate();
        var sb = new StringBuilderWrapper(sbInner);
        var includeOptions = !WithoutOptions && request.QueryString[nameof(WithoutOptions)] == null;
        if (includeOptions)
        {
            sb.AppendLine("/* Options:");
            sb.AppendLine("Date: {0}".Fmt(DateTime.Now.ToString("s").Replace("T", " ")));
            sb.AppendLine("Version: {0}".Fmt(Env.VersionString));
            sb.AppendLine("Tip: {0}".Fmt(HelpMessages.NativeTypesDtoOptionsTip.Fmt("//")));
            sb.AppendLine("BaseUrl: {0}".Fmt(Config.BaseUrl));
            if (Config.UsePath != null)
                sb.AppendLine("UsePath: {0}".Fmt(Config.UsePath));

            sb.AppendLine();
            sb.AppendLine("{0}AddServiceStackTypes: {1}".Fmt(defaultValue("AddServiceStackTypes"), Config.AddServiceStackTypes));
            sb.AppendLine("{0}AddDescriptionAsComments: {1}".Fmt(defaultValue("AddDescriptionAsComments"), Config.AddDescriptionAsComments));
            sb.AppendLine("{0}IncludeTypes: {1}".Fmt(defaultValue("IncludeTypes"), Config.IncludeTypes.Safe().ToArray().Join(",")));
            sb.AppendLine("{0}ExcludeTypes: {1}".Fmt(defaultValue("ExcludeTypes"), Config.ExcludeTypes.Safe().ToArray().Join(",")));
            sb.AppendLine("{0}DefaultImports: {1}".Fmt(defaultValue("DefaultImports"), defaultImports.Join(",")));
            AddQueryParamOptions.Each(name => sb.AppendLine($"{defaultValue(name)}{name}: {request.QueryString[name]}"));

            sb.AppendLine("*/");
            sb.AppendLine();
        }

        string lastNS = null;

        var existingTypes = new HashSet<string>();

        var requestTypes = metadata.Operations.Select(x => x.Request).ToSet();
        var requestTypesMap = metadata.Operations.ToSafeDictionary(x => x.Request);
        var responseTypes = metadata.Operations
            .Where(x => x.Response != null)
            .Select(x => x.Response).ToSet();
        var types = metadata.Types.CreateSortedTypeList();

        AllTypes = metadata.GetAllTypesOrdered();
        AllTypes.RemoveAll(x => x.IgnoreType(Config, includeList));
        AllTypes = FilterTypes(AllTypes);

        //TypeScript doesn't support reusing same type name with different generic airity
        var conflictPartialNames = AllTypes.Map(x => x.Name).Distinct()
            .GroupBy(g => g.LeftPart('`'))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        this.conflictTypeNames = AllTypes
            .Where(x => conflictPartialNames.Any(name => x.Name.StartsWith(name)))
            .Map(x => x.Name);

        var insertCode = InsertCodeFilter?.Invoke(AllTypes, Config);
        if (insertCode != null)
            sb.AppendLine(insertCode);

        
        sb.AppendLine(@"""use strict"";");
        
        //ServiceStack core interfaces
        foreach (var type in AllTypes)
        {
            var fullTypeName = type.GetFullName();
            if (requestTypes.Contains(type))
            {
                if (!existingTypes.Contains(fullTypeName))
                {
                    MetadataType response = null;
                    if (requestTypesMap.TryGetValue(type, out var operation))
                    {
                        response = operation.Response;
                    }

                    lastNS = AppendType(ref sb, type, lastNS,
                        new CreateTypeOptions
                        {
                            Routes = metadata.Operations.GetRoutes(type),
                            ImplementsFn = () =>
                            {
                                if (!Config.AddReturnMarker
                                    && operation?.ReturnsVoid != true
                                    && operation?.ReturnType == null)
                                    return null;

                                if (operation?.ReturnsVoid == true)
                                    return nameof(IReturnVoid);
                                if (operation?.ReturnType != null)
                                {
                                    var returnType = Gen.Type("IReturn`1", new[]
                                    {
                                        TypeScriptGenerator.ReturnTypeAliases.TryGetValue(operation.ReturnType.Name, out var returnTypeAlias)
                                            ? returnTypeAlias
                                            : Type(operation.ReturnType.Name, operation.ReturnType.GenericArgs)
                                    });
                                    return returnType;
                                }
                                return response != null
                                    ? Gen.Type("IReturn`1", new[] { Gen.Type(response.Name, response.GenericArgs) })
                                    : null;
                            },
                            IsRequest = true,
                            Op = operation,
                        });

                    existingTypes.Add(fullTypeName);
                }
            }
            else if (responseTypes.Contains(type))
            {
                if (!existingTypes.Contains(fullTypeName)
                    && !Config.IgnoreTypesInNamespaces.Contains(type.Namespace))
                {
                    lastNS = AppendType(ref sb, type, lastNS,
                        new CreateTypeOptions
                        {
                            IsResponse = true,
                        });

                    existingTypes.Add(fullTypeName);
                }
            }
            else if (types.Contains(type) && !existingTypes.Contains(fullTypeName))
            {
                lastNS = AppendType(ref sb, type, lastNS,
                    new CreateTypeOptions { IsType = true });

                existingTypes.Add(fullTypeName);
            }
        }

        var addCode = AddCodeFilter?.Invoke(AllTypes, Config);
        if (addCode != null)
            sb.AppendLine(addCode);

        sb.AppendLine(); //tslint

        return StringBuilderCache.ReturnAndFree(sbInner);
    }

    private string AppendType(ref StringBuilderWrapper sb, MetadataType type, string lastNS,
        CreateTypeOptions options)
    {
        if (!type.IsInterface.GetValueOrDefault() || type.IsEnum.GetValueOrDefault())
        {
            var typeName = Type(type.Name, type.GenericArgs);
            
            if (type.IsEnum.GetValueOrDefault())
            {
                sb.AppendLine($"export var {typeName};");
                sb.AppendLine("(function (" + typeName + ") {");
                sb = sb.Indent();

                if (type.EnumNames != null)
                {
                    for (var i = 0; i < type.EnumNames.Count; i++)
                    {
                        var name = type.EnumNames[i];
                        var value = type.EnumValues?[i];
                        var memberValue = type.GetEnumMemberValue(i); 
                        var strValue = memberValue ?? name;

                        if (value == null || memberValue != null)
                        {
                            sb.AppendLine($"{typeName}[\"{name}\"] = \"{strValue}\";");
                        }
                        else
                        {
                            sb.AppendLine($"{typeName}[{typeName}[\"{name}\"] = {value}] = \"{name}\";");
                        }
                    }
                }
                sb = sb.UnIndent();
                sb.AppendLine("})(" + typeName + " || (" + typeName + " = {}));");
            }
            else
            {
                string addDeclaration = null;
                var extendsType = type.Inherits != null
                    ? DeclarationType(type.Inherits.Name, type.Inherits.GenericArgs, out addDeclaration)
                    : "";
                
                if (addDeclaration != null && !AddedDeclarations.Contains(addDeclaration))
                {
                    AddedDeclarations.Add(addDeclaration);
                    sb.AppendLine(addDeclaration);
                }

                sb.AppendLine(extendsType.Length > 0
                    ? "export class " + typeName + " extends " + extendsType + " {"
                    : "export class " + typeName + " {");
                sb = sb.Indent();
                sb.AppendLine(extendsType.Length > 0
                    ? "constructor(init) { super(init); Object.assign(this, init); }"
                    : "constructor(init) { Object.assign(this, init); }");

                string responseTypeExpression = options?.Op != null
                    ? "createResponse () { };"
                    : null;

                //Request DTO props...
                AddProperties(sb, type,
                    includeResponseStatus: Config.AddResponseStatus && options.IsResponse
                    && type.Properties.Safe().All(x => x.Name != nameof(ResponseStatus)));
                
                var implStr = options?.ImplementsFn?.Invoke();
                if (!string.IsNullOrEmpty(implStr))
                {
                    if (implStr.StartsWith("IReturn<") || implStr == "IReturnVoid")
                    {
                        if (implStr.StartsWith("IReturn<"))
                        {
                            var types = implStr.RightPart('<');
                            var returnType = types.Substring(0, types.Length - 1);

                            var responseName = GetReturnType(returnType);
                            responseTypeExpression = "createResponse() { return " + responseName + "; };";
                        }
                        else if (implStr == "IReturnVoid")
                        {
                            responseTypeExpression = "createResponse() { };";
                        }
                    }
                }
                if (options?.Op != null)
                    sb.AppendLine("getTypeName() { return '" + typeName +  "'; };");
                if (options?.Op?.Method != null)
                    sb.AppendLine("getMethod() { return '" + options.Op.Method + "'; };");
                if (responseTypeExpression != null)
                    sb.AppendLine(responseTypeExpression);
                sb.AppendLine($"return {typeName};");

                sb = sb.UnIndent();
                sb.AppendLine("}");
            }
        }
        
        return lastNS;
    }
    
    public void AddProperties(StringBuilderWrapper sb, MetadataType type, bool includeResponseStatus)
    {
        var wasAdded = false;

        if (type.Properties != null)
        {
            foreach (var prop in type.Properties)
            {
                sb.AppendLine(GetPropertyName(prop) + ";");
            }
        }

        if (includeResponseStatus)
        {
            sb.AppendLine("ResponseStatus;");
        }
    }
    
    public string GetPropertyName(MetadataPropertyType prop) => 
        prop.GetSerializedAlias() ?? prop.Name.SafeToken().PropertyStyle();


    // Used in createResponse(){ return ... } 
    // Needs to be typed erased
    private string GetReturnType(string originalReturnType)
    {
        var returnType = ReturnTypeFilter(originalReturnType);
        
        // This is to avoid invalid syntax such as "return new string()"
        TypeScriptGenerator.primitiveDefaultValues.TryGetValue(returnType, out var replaceReturnType);

        if (returnType == "any")
            replaceReturnType = "{}";
        else if (returnType.EndsWith("[]"))
            replaceReturnType = "[]";
        else if (returnType.StartsWith("{")) // { [name:K]: V }
            replaceReturnType = "{}";

        var responseName = replaceReturnType ?? $"new {returnType}()";
        return responseName;
    }

    public static string CreateEmptyClass(string name) => "class " + name + @" {}";

}
