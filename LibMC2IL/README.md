# LibMC2IL

`LibMC2IL` is the low-level metadata and binary parsing library used by MC2IL. Most users should start with the [main README](../README.md) and the command-line application.

## Build and initialize

Build the solution from its root directory:

```sh
dotnet build MC2IL.slnx --configuration Release
```

Load a game from files and keep its state in a `LibMC2ILContext`:

```csharp
using System;
using AssetRipper.Primitives;
using LibMC2IL;

var unityVersion = new UnityVersion(2019, 4, 34, UnityVersionType.Final, 1);
LibMC2ILContext context = LibMC2ILMain.LoadFromFileAsContext(
    gameAssemblyPath,
    globalMetadataPath,
    unityVersion);
```

For data already loaded into memory, use `LibMC2ILMain.InitializeAsContext(binaryBytes, metadataBytes, unityVersion)`. The context exposes the parsed binary and metadata, and keeps lookups scoped to one game.

## Global constants

IL2CPP stores type, field, string literal, and method references as globals. Given the virtual address of one of these globals - for example a type reference is passed into every call to il2cpp_codegen_object_new - you can use LibMC2IL to get the associated object.

### Type References

```c#
var type = context.GetTypeGlobalByAddress(0x180623548)
    ?? throw new InvalidOperationException("No type reference exists at this address.");
```

### Method References

Method references can refer to generic methods. Use the metadata usage to inspect the referenced method and its generic parameters. Concrete native implementations are available through `context.Binary.ConcreteGenericMethods` and `context.Binary.ConcreteGenericImplementationsByAddress` when the binary contains them.

If you just want method details, use this:
```c#
var method = context.GetMethodDefinitionByGlobalAddress(0x182938239)
    ?? throw new InvalidOperationException("No method reference exists at this address.");
```

If you want more complex data, such as type and/or method generic params, you can use this code to get the raw global struct, and obtain the generic data like so:
```c#
var usage = context.GetMethodGlobalByAddress(0x182938239)
    ?? throw new InvalidOperationException("No method usage exists at this address.");

if(usage.Type == MetadataUsageType.MethodRef) {
    var genericMethodRef = usage.AsGenericMethodRef();
    Console.WriteLine(genericMethodRef.declaringType); //Il2CppTypeDefinition
    Console.WriteLine(genericMethodRef.baseMethod); //Il2CppMethodDefinition, equal to the one returned by the above method
    Console.WriteLine(genericMethodRef.typeGenericParams); //Il2CppTypeReflectionData[]
    Console.WriteLine(genericMethodRef.methodGenericParams); //Il2CppTypeReflectionData[]
} else {
    //Method is not generic
    Console.WriteLine(usage.AsMethod()); //Il2CppMethodDefinition
}
```

### Field References

```c#
var fieldDef = context.GetFieldGlobalByAddress(0x182933215)
    ?? throw new InvalidOperationException("No field reference exists at this address.");
```

### String Literals

```c#
string literal = context.GetLiteralByAddress(0x182197654)
    ?? throw new InvalidOperationException("No string literal exists at this address.");
```

## Reflection API

LibMC2IL provides utility methods to get types by name in order to start the Reflection process.

```c#
//Signature:
var type = context.ReflectionCache.GetType(typeName, optionalNamespaceName);

//Examples:
var stringType = context.ReflectionCache.GetType("String")
    ?? throw new InvalidOperationException("System.String was not found.");
var listType = context.ReflectionCache.GetType("List`1")
    ?? throw new InvalidOperationException("System.Collections.Generic.List`1 was not found.");
var objectType = context.ReflectionCache.GetType("Object", "UnityEngine")
    ?? throw new InvalidOperationException("UnityEngine.Object was not found.");
```

## Accessing type properties

Convenience methods are provided to obtain the most commonly used properties of a type, such as its methods, fields, properties, and events, in addition to its name, namespace, and hierarchy data such as declaring type, base class, interfaces etc.

Given the definition of `type` as such:
```c#
var type = context.ReflectionCache.GetType("String")
    ?? throw new InvalidOperationException("System.String was not found.");
```

### Tokens

As a quick note, all fields, types, methods, properties, and events store their token in the `token` field.

### Basic Properties
```c#
Console.WriteLine(type.Namespace); //System
Console.WriteLine(type.Name); //String
Console.WriteLine(type.FullName); //System.String
```

### Inheritance
```c#
//Base class
Console.WriteLine(type.BaseType.FullName); //System.Object

//Interface data, including generic parameters.

//Il2CppTypeReflectionData is a wrapper around Il2CppTypeDefinition that allows for generics.
//ToString on these returns their canonical form.
Il2CppTypeReflectionData[] interfaces = type.Interfaces;
var enumerableOfChar = interfaces[5];
Console.WriteLine(enumerableOfChar); //System.Collections.Generic.IEnumerable`1<System.Char>
Console.WriteLine(enumerableOfChar.isType); //true
Console.WriteLine(enumerableOfChar.isGenericType); //true
Console.WriteLine(enumerableOfChar.baseType.FullName); //System.Collections.Generic.IEnumerable`1
Console.WriteLine(enumerableOfChar.genericParams[0]); //System.Char
Console.WriteLine(enumerableOfChar.genericParams[0].isType); //true
Console.WriteLine(enumerableOfChar.genericParams[0].isGenericType); //false
```

### Methods
```c#
//In this example, string.Join(string separator, string[] value) is the first-defined method in the metadata, but it could be a different order.
var join = type.Methods[0];

Console.WriteLine(join.Name); //Join
Console.WriteLine($"0x{join.MethodPointer:X}"); //0x180385033
//Getting the file address of a method
Console.WriteLine($"Join is in-assembly at address 0x{context.Binary.MapVirtualAddressToRaw(join.MethodPointer):X}"); //Join is in-assembly at address 0x385033
//ReturnType is a ReflectionData again, like interfaces are
Console.WriteLine(join.ReturnType); //System.String
//DeclaringType gives you the original Il2CppTypeDefinition back
Console.WriteLine(join.DeclaringType.FullName); //System.String
//Parameters are Il2CppParameterReflectionData objects, objects to contain information on a parameter, such as its name, type, and default value.
//ToString on these also returns their canonical form.
Console.WriteLine(join.Parameters[0].Type) //System.String
Console.WriteLine(join.Parameters[1].Type) //System.String[]
Console.WriteLine(join.Parameters[1].Type.isType) //false
Console.WriteLine(join.Parameters[1].Type.isArray) //true
Console.WriteLine(join.Parameters[1].Type.arrayType) //System.String
```

### Fields
```c#
//Accessing the string's internal length field. Note that unlike methods, fields DO have a defined order and they are presented in that order.
var lengthField = type.Fields[0];
Console.WriteLine(lengthField.Name); //m_stringLength
//FieldType is an Il2CppTypeReflectionData, the ToString of which gives the name of the class.
Console.WriteLine(lengthField.FieldType); //System.Int32
```

### Properties

```c#
var lengthProperty = type.Properties[1];
Console.WriteLine(lengthProperty.Name); //Length
Console.WriteLine(lengthProperty.Getter.Name); //get_Length
Console.WriteLine(lengthProperty.Setter); //null
//PropertyType is an Il2CppTypeReflectionData object
Console.WriteLine(lengthProperty.PropertyType); //System.Int32
Console.WriteLine(lengthProperty.DeclaringType.FullName); //System.String
```

### Nested Types
```c#
var transform = context.ReflectionCache.GetType("Transform", "UnityEngine")
    ?? throw new InvalidOperationException("UnityEngine.Transform was not found.");
Console.WriteLine(transform.NestedTypes[0].Name); //Enumerator
Console.WriteLine(transform.NestedTypes[0].DeclaringType.FullName); //UnityEngine.Transform
```

### Events
```c#
var appDomain = context.ReflectionCache.GetType("AppDomain", "System")
    ?? throw new InvalidOperationException("System.AppDomain was not found.");
Console.WriteLine(appDomain.Events.Length); //3
Console.WriteLine(appDomain.Events[0].Name); //DomainUnload
Console.WriteLine(appDomain.Events[0].EventType); //System.EventHandler
//Adder returns an Il2CppMethodDefinition and represents the method used to add a listener to the event.
//Remover also exists, as does Invoker.
Console.WriteLine(appDomain.Events[0].Adder.Name); //add_DomainUnload

//Type returns an Il2CppTypeReflectionData, as it's just a standard method parameter.
Console.WriteLine(appDomain.Events[0].Adder.Parameters[0].Type); //System.EventHandler
```

### Generic Methods

Due to the nature of structures in C++, generic methods have to have variants based on the size (in bytes) of their generic parameters.

Consider a 64-bit assembly. The size of an object (which would be 8 bytes, as it's a pointer) and an `int`, or rather, a `System.Int32` (which would be 4 bytes) are different.

So `List<T>` needs to have different implementations of its methods depending on if it's a `List<Some Object>` versus a `List<int>`.

And IL2CPP strips out any implementations that the game itself doesn't - and won't ever - use.

So, given an `Il2CppMethodDefinition` for a generic method, you can find out which implementations DO exist by accessing the ConcreteGenericMethods dict on a PE object, like so.

```c#
var listType = context.ReflectionCache.GetType("List`1", "System")
    ?? throw new InvalidOperationException("System.Collections.Generic.List`1 was not found.");
var addMethod = listType.Methods.First(m => m.Name == "Add");
var variants = context.Binary.ConcreteGenericMethods[addMethod];
Console.WriteLine(addMethod.MethodPointer); //0xdeadbeef
Console.WriteLine(variants[0].BaseMethod.MethodPointer); //Same as above
Console.WriteLine(variants[0].GenericParams[0]); //Could be, for example, "System.Int32"
//The below may or may not give the original method pointer.
//The original pointer is the implementation for `T`.
//This will be the same as the implementation for `object`
//This variant, assuming it is for Int32, should be a different pointer.
//If it's for a class such as `string`, it will be the same pointer.
Console.WriteLine(variants[0].GenericVariantPtr); //0x123456789
```   

Alternatively, if you have a call to an address 0x123456789 and you know there's no defined method at that address, you can check for generic implementations and obtain the base definition by using the following code:
```c#
var genericImplementations = context.Binary.ConcreteGenericImplementationsByAddress[0x123456789];
Console.WriteLine(genericImplementations[0].BaseMethod.HumanReadableSignature);
```
