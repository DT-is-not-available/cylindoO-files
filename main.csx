//1
using System.Text;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Diagnostics;


// JSON PARSER
// drop in for System.Text.Json (minimal libraries available for some reason)
// thanks to https://github.com/zanders3/json

// Really simple JSON parser in ~300 lines
// - Attempts to parse JSON files with minimal GC allocation
// - Nice and simple "[1,2,3]".FromJson<List<int>>() API
// - Classes and structs can be parsed too!
//      class Foo { public int Value; }
//      "{\"Value\":10}".FromJson<Foo>()
// - Can parse JSON without type information into Dictionary<string,object> and List<object> e.g.
//      "[1,2,3]".FromJson<object>().GetType() == typeof(List<object>)
//      "{\"Value\":10}".FromJson<object>().GetType() == typeof(Dictionary<string,object>)
// - No JIT Emit support to support AOT compilation on iOS
// - Attempts are made to NOT throw an exception if the JSON is corrupted or invalid: returns null instead.
// - Only public fields and property setters on classes/structs will be written to
//
// Limitations:
// - No JIT Emit support to parse structures quickly
// - Limited to parsing <2GB JSON files (due to int.MaxValue)
// - Parsing of abstract classes or interfaces is NOT supported and will throw an exception.
[ThreadStatic] static Stack<List<string>> splitArrayPool;
[ThreadStatic] static StringBuilder stringBuilder;
[ThreadStatic] static Dictionary<Type, Dictionary<string, FieldInfo>> fieldInfoCache;
[ThreadStatic] static Dictionary<Type, Dictionary<string, PropertyInfo>> propertyInfoCache;

public static T FromJson<T>(this string json)
{
	// Initialize, if needed, the ThreadStatic variables
	if (propertyInfoCache == null) propertyInfoCache = new Dictionary<Type, Dictionary<string, PropertyInfo>>();
	if (fieldInfoCache == null) fieldInfoCache = new Dictionary<Type, Dictionary<string, FieldInfo>>();
	if (stringBuilder == null) stringBuilder = new StringBuilder();
	if (splitArrayPool == null) splitArrayPool = new Stack<List<string>>();

	//Remove all whitespace not within strings to make parsing simpler
	stringBuilder.Length = 0;
	for (int i = 0; i < json.Length; i++)
	{
		char c = json[i];
		if (c == '"')
		{
			i = AppendUntilStringEnd(true, i, json);
			continue;
		}
		if (char.IsWhiteSpace(c))
			continue;

		stringBuilder.Append(c);
	}

	//Parse the thing!
	return (T)ParseValue(typeof(T), stringBuilder.ToString());
}

static int AppendUntilStringEnd(bool appendEscapeCharacter, int startIdx, string json)
{
	stringBuilder.Append(json[startIdx]);
	for (int i = startIdx + 1; i < json.Length; i++)
	{
		if (json[i] == '\\')
		{
			if (appendEscapeCharacter)
				stringBuilder.Append(json[i]);
			stringBuilder.Append(json[i + 1]);
			i++;//Skip next character as it is escaped
		}
		else if (json[i] == '"')
		{
			stringBuilder.Append(json[i]);
			return i;
		}
		else
			stringBuilder.Append(json[i]);
	}
	return json.Length - 1;
}

//Splits { <value>:<value>, <value>:<value> } and [ <value>, <value> ] into a list of <value> strings
static List<string> Split(string json)
{
	List<string> splitArray = splitArrayPool.Count > 0 ? splitArrayPool.Pop() : new List<string>();
	splitArray.Clear();
	if (json.Length == 2)
		return splitArray;
	int parseDepth = 0;
	stringBuilder.Length = 0;
	for (int i = 1; i < json.Length - 1; i++)
	{
		switch (json[i])
		{
			case '[':
			case '{':
				parseDepth++;
				break;
			case ']':
			case '}':
				parseDepth--;
				break;
			case '"':
				i = AppendUntilStringEnd(true, i, json);
				continue;
			case ',':
			case ':':
				if (parseDepth == 0)
				{
					splitArray.Add(stringBuilder.ToString());
					stringBuilder.Length = 0;
					continue;
				}
				break;
		}

		stringBuilder.Append(json[i]);
	}

	splitArray.Add(stringBuilder.ToString());

	return splitArray;
}

internal static object ParseValue(Type type, string json)
{
	if (type == typeof(string))
	{
		if (json.Length <= 2)
			return string.Empty;
		StringBuilder parseStringBuilder = new StringBuilder(json.Length);
		for (int i = 1; i < json.Length - 1; ++i)
		{
			if (json[i] == '\\' && i + 1 < json.Length - 1)
			{
				int j = "\"\\nrtbf/".IndexOf(json[i + 1]);
				if (j >= 0)
				{
					parseStringBuilder.Append("\"\\\n\r\t\b\f/"[j]);
					++i;
					continue;
				}
				if (json[i + 1] == 'u' && i + 5 < json.Length - 1)
				{
					UInt32 c = 0;
					if (UInt32.TryParse(json.Substring(i + 2, 4), System.Globalization.NumberStyles.AllowHexSpecifier, null, out c))
					{
						parseStringBuilder.Append((char)c);
						i += 5;
						continue;
					}
				}
			}
			parseStringBuilder.Append(json[i]);
		}
		return parseStringBuilder.ToString();
	}
	if (type.IsPrimitive)
	{
		var result = Convert.ChangeType(json, type, System.Globalization.CultureInfo.InvariantCulture);
		return result;
	}
	if (type == typeof(decimal))
	{
		decimal result;
		decimal.TryParse(json, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
		return result;
	}
	if (type == typeof(DateTime))
	{
		DateTime result;
		DateTime.TryParse(json.Replace("\"",""), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result);
		return result;
	}
	if (json == "null")
	{
		return null;
	}
	if (type.IsEnum)
	{
		if (json[0] == '"')
			json = json.Substring(1, json.Length - 2);
		try
		{
			return Enum.Parse(type, json, false);
		}
		catch
		{
			return 0;
		}
	}
	if (type.IsArray)
	{
		Type arrayType = type.GetElementType();
		if (json[0] != '[' || json[json.Length - 1] != ']')
			return null;

		List<string> elems = Split(json);
		Array newArray = Array.CreateInstance(arrayType, elems.Count);
		for (int i = 0; i < elems.Count; i++)
			newArray.SetValue(ParseValue(arrayType, elems[i]), i);
		splitArrayPool.Push(elems);
		return newArray;
	}
	if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
	{
		Type listType = type.GetGenericArguments()[0];
		if (json[0] != '[' || json[json.Length - 1] != ']')
			return null;

		List<string> elems = Split(json);
		var list = (IList)type.GetConstructor(new Type[] { typeof(int) }).Invoke(new object[] { elems.Count });
		for (int i = 0; i < elems.Count; i++)
			list.Add(ParseValue(listType, elems[i]));
		splitArrayPool.Push(elems);
		return list;
	}
	if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
	{
		Type keyType, valueType;
		{
			Type[] args = type.GetGenericArguments();
			keyType = args[0];
			valueType = args[1];
		}

		//Refuse to parse dictionary keys that aren't of type string
		if (keyType != typeof(string))
			return null;
		//Must be a valid dictionary element
		if (json[0] != '{' || json[json.Length - 1] != '}')
			return null;
		//The list is split into key/value pairs only, this means the split must be divisible by 2 to be valid JSON
		List<string> elems = Split(json);
		if (elems.Count % 2 != 0)
			return null;

		var dictionary = (IDictionary)type.GetConstructor(new Type[] { typeof(int) }).Invoke(new object[] { elems.Count / 2 });
		for (int i = 0; i < elems.Count; i += 2)
		{
			if (elems[i].Length <= 2)
				continue;
			string keyValue = elems[i].Substring(1, elems[i].Length - 2);
			object val = ParseValue(valueType, elems[i + 1]);
			dictionary[keyValue] = val;
		}
		return dictionary;
	}
	if (type == typeof(object))
	{
		return ParseAnonymousValue(json);
	}
	if (json[0] == '{' && json[json.Length - 1] == '}')
	{
		return ParseObject(type, json);
	}

	return null;
}

static object ParseAnonymousValue(string json)
{
	if (json.Length == 0)
		return null;
	if (json[0] == '{' && json[json.Length - 1] == '}')
	{
		List<string> elems = Split(json);
		if (elems.Count % 2 != 0)
			return null;
		var dict = new Dictionary<string, object>(elems.Count / 2);
		for (int i = 0; i < elems.Count; i += 2)
			dict[elems[i].Substring(1, elems[i].Length - 2)] = ParseAnonymousValue(elems[i + 1]);
		return dict;
	}
	if (json[0] == '[' && json[json.Length - 1] == ']')
	{
		List<string> items = Split(json);
		var finalList = new List<object>(items.Count);
		for (int i = 0; i < items.Count; i++)
			finalList.Add(ParseAnonymousValue(items[i]));
		return finalList;
	}
	if (json[0] == '"' && json[json.Length - 1] == '"')
	{
		string str = json.Substring(1, json.Length - 2);
		return str.Replace("\\", string.Empty);
	}
	if (char.IsDigit(json[0]) || json[0] == '-')
	{
		if (json.Contains("."))
		{
			double result;
			double.TryParse(json, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
			return result;
		}
		else
		{
			int result;
			int.TryParse(json, out result);
			return result;
		}
	}
	if (json == "true")
		return true;
	if (json == "false")
		return false;
	// handles json == "null" as well as invalid JSON
	return null;
}

static Dictionary<string, T> CreateMemberNameDictionary<T>(T[] members) where T : MemberInfo
{
	Dictionary<string, T> nameToMember = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
	for (int i = 0; i < members.Length; i++)
	{
		T member = members[i];
		if (member.IsDefined(typeof(IgnoreDataMemberAttribute), true))
			continue;

		string name = member.Name;
		if (member.IsDefined(typeof(DataMemberAttribute), true))
		{
			DataMemberAttribute dataMemberAttribute = (DataMemberAttribute)Attribute.GetCustomAttribute(member, typeof(DataMemberAttribute), true);
			if (!string.IsNullOrEmpty(dataMemberAttribute.Name))
				name = dataMemberAttribute.Name;
		}

		nameToMember.Add(name, member);
	}

	return nameToMember;
}

static object ParseObject(Type type, string json)
{
	object instance = FormatterServices.GetUninitializedObject(type);

	//The list is split into key/value pairs only, this means the split must be divisible by 2 to be valid JSON
	List<string> elems = Split(json);
	if (elems.Count % 2 != 0)
		return instance;

	Dictionary<string, FieldInfo> nameToField;
	Dictionary<string, PropertyInfo> nameToProperty;
	if (!fieldInfoCache.TryGetValue(type, out nameToField))
	{
		nameToField = CreateMemberNameDictionary(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy));
		fieldInfoCache.Add(type, nameToField);
	}
	if (!propertyInfoCache.TryGetValue(type, out nameToProperty))
	{
		nameToProperty = CreateMemberNameDictionary(type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy));
		propertyInfoCache.Add(type, nameToProperty);
	}

	for (int i = 0; i < elems.Count; i += 2)
	{
		if (elems[i].Length <= 2)
			continue;
		string key = elems[i].Substring(1, elems[i].Length - 2);
		string value = elems[i + 1];

		FieldInfo fieldInfo;
		PropertyInfo propertyInfo;
		if (nameToField.TryGetValue(key, out fieldInfo))
			fieldInfo.SetValue(instance, ParseValue(fieldInfo.FieldType, value));
		else if (nameToProperty.TryGetValue(key, out propertyInfo))
			propertyInfo.SetValue(instance, ParseValue(propertyInfo.PropertyType, value), null);
	}

	return instance;
}

string list = "\n";

class ModConfig {
	public string? Name {get; set;}
	public string? Description {get; set;}
	public Dictionary<string, string[]> Events {get; set;}
};

EnsureDataLoaded();

UndertaleFunction DefineFunc(string name)
{
    var str = Data.Strings.MakeString(name);
    var func = new UndertaleFunction()
    {
        Name = str,
        NameStringID = Data.Strings.IndexOf(str)
    };
    Data.Functions.Add(func);
    return func;
}

Data.GeneralInfo.DisplayName.Content += " (running cylindoO)";

Directory.CreateDirectory("./patches");
Directory.CreateDirectory("./mods");

ThreadLocal<GlobalDecompileContext> DECOMPILE_CONTEXT = new ThreadLocal<GlobalDecompileContext>(() => new GlobalDecompileContext(Data, false));

/*
ScriptMessage("Patching game...");
for (int i = 0; i < Data.Code.Count; i++) {
	UndertaleCode code = Data.Code[i];
	if (File.Exists("./patches/"+code.Name.Content+".gml")) {
		string file = File.ReadAllText("./patches/"+code.Name.Content+".gml");
		code.ReplaceGML(Decompiler.Decompile(code, DECOMPILE_CONTEXT.Value)+"\n"+file, Data);
		ChangeSelection(code);
		list += code.Name.Content+"\n";
	}
}
// ScriptMessage(Data.Code[0].Name.Content+":\n\n"+Decompiler.Decompile(Data.Code[0], DECOMPILE_CONTEXT.Value));
ScriptMessage("Scripts patched:"+list);
*/

public string ReplaceFirst(string text, string search, string replace)
{
	int pos = text.IndexOf(search);
	if (pos < 0)
	{
		return text;
	}
	return text.Substring(0, pos) + replace + text.Substring(pos + search.Length);
}

ScriptMessage("Compiling...");
foreach (string dir in Directory.GetDirectories(".\\mods")) {
	ScriptMessage("Loading "+dir);
	ModConfig config = FromJson<ModConfig>(File.ReadAllText(dir+"/mod.json"));
	foreach(KeyValuePair<string, string[]> entry in config.Events)
	{
		ScriptMessage(entry.Key + ": ["+string.Join(", ", entry.Value)+"]");
		string[] parts = entry.Key.Split(".");
		string evID;
		string obj;
		if (parts.Length == 1) {
			obj = "obj_renderer";
			evID = parts[0];
		} else {
			evID = parts[1];
			obj = parts[0];
		}
		var game_obj = Data.GameObjects.ByName(obj);
		foreach(string filepath in entry.Value) {
			string[] args = filepath.Split("@");
			string scriptfile = File.ReadAllText(dir+"/"+args[0]);
			switch (evID) {
				case "Step":
					game_obj.EventHandlerFor(EventType.Step, EventSubtypeStep.Step, Data.Strings, Data.Code, Data.CodeLocals).AppendGML(scriptfile, Data);
				break;
				case "StepBegin":
					game_obj.EventHandlerFor(EventType.Step, EventSubtypeStep.BeginStep, Data.Strings, Data.Code, Data.CodeLocals).AppendGML(scriptfile, Data);
				break;
				case "StepEnd":
					game_obj.EventHandlerFor(EventType.Step, EventSubtypeStep.EndStep, Data.Strings, Data.Code, Data.CodeLocals).AppendGML(scriptfile, Data);
				break;
				case "Draw":
					game_obj.EventHandlerFor(EventType.Draw, EventSubtypeDraw.Draw, Data.Strings, Data.Code, Data.CodeLocals).AppendGML(scriptfile, Data);
				break;
				case "DrawBegin":
					game_obj.EventHandlerFor(EventType.Draw, EventSubtypeDraw.DrawBegin, Data.Strings, Data.Code, Data.CodeLocals).AppendGML(scriptfile, Data);
				break;
				case "DrawEnd":
					game_obj.EventHandlerFor(EventType.Draw, EventSubtypeDraw.DrawEnd, Data.Strings, Data.Code, Data.CodeLocals).AppendGML(scriptfile, Data);
				break;
				case "DrawGUIBegin":
					game_obj.EventHandlerFor(EventType.Draw, EventSubtypeDraw.DrawGUIBegin, Data.Strings, Data.Code, Data.CodeLocals).AppendGML(scriptfile, Data);
				break;
				case "DrawGUIEnd":
					game_obj.EventHandlerFor(EventType.Draw, EventSubtypeDraw.DrawGUIEnd, Data.Strings, Data.Code, Data.CodeLocals).AppendGML(scriptfile, Data);
				break;
				case "Append":
					// AppendGML wont work for some reason
					Data.Code.ByName(obj).ReplaceGML(Decompiler.Decompile(Data.Code.ByName(obj), DECOMPILE_CONTEXT.Value)+"\n"+scriptfile, Data);
				break;
				case "Replace":
					Data.Code.ByName(obj).ReplaceGML(ReplaceFirst(Decompiler.Decompile(Data.Code.ByName(obj), DECOMPILE_CONTEXT.Value), args[1], scriptfile), Data);
				break;
				case "Overwrite":
					Data.Code.ByName(obj).ReplaceGML(scriptfile, Data);
				break;
				case "OverwriteASM":
                	Data.Code.ByName(obj).Replace(Assembler.Assemble(scriptfile, Data.Functions, Data.Variables, Data.Strings, Data));
				break;
				default:
					ScriptMessage("Unknown event '"+evID+"'");
				break;
			}
		}
	}
}