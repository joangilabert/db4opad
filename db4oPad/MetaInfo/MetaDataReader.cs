using System;
using System.Collections.Generic;
using System.Linq;
using Db4objects.Db4o;
using Db4objects.Db4o.Ext;
using Db4objects.Db4o.Reflect;
using Db4objects.Db4o.Reflect.Generic;
using Gamlor.Db4oPad.Utils;

namespace Gamlor.Db4oPad.MetaInfo
{
    internal class MetaDataReader
    {
        private readonly TypeResolver typeResolver;
        private readonly IDictionary<string, CachedStoredClass> cachedStoredClasses;

        private MetaDataReader(TypeResolver typeResolver, IDictionary<string, CachedStoredClass> cachedStoredClasses)
        {
            this.typeResolver = typeResolver;
            this.cachedStoredClasses = cachedStoredClasses;
        }

        public static IEnumerable<ITypeDescription> Read(IObjectContainer database)
        {
            return Read(database, TypeLoader.DefaultTypeResolver());
        }

        public static IEnumerable<ITypeDescription> Read(IObjectContainer database,
                                                         TypeResolver typeResolver)
        {
            new {database, typeResolver}.CheckNotNull();
            var reader = new MetaDataReader(typeResolver, ExtractStoredClasses(database.Ext().StoredClasses()));
            return reader.CreateTypes(database.Ext()).ToList();
        }


        private IEnumerable<ITypeDescription> CreateTypes(IExtObjectContainer container)
        {
            var reflectClasses = container.KnownClasses();
            var allKnownClasses = reflectClasses.Distinct().ToArray();
            var typeMap = new Dictionary<string, ITypeDescription>();
            foreach (var classInfo in allKnownClasses)
            {
                CreateType(classInfo, c => FindReflectClass(c, allKnownClasses), typeMap);
            }
            return typeMap.Select(t => t.Value);
        }

        private Maybe<IReflectClass> FindReflectClass(TypeName typeName,
            IReflectClass[] allKnownClasses)
        {
            return (from c in allKnownClasses
                    where c.GetName() == typeName.FullName
                    select c).FirstMaybe();
        }

        /// <summary>
        /// For some types there is no type info available from db4o. Therefore we just create an empty class.
        /// </summary>
        private ITypeDescription MockTypeFor(TypeName typeName,
                                            IDictionary<string, ITypeDescription> knownTypes)
        {
            var type= SimpleClassDescription.Create(typeName, t => Enumerable.Empty<SimpleFieldDescription>());
            knownTypes[typeName.FullName] = type;
            return type;
        }

        private IndexingState IndexLookUp(TypeName declaringtype, string fieldName, TypeName fieldtype)
        {
            var storedInfo = cachedStoredClasses.TryGet(declaringtype.FullName);
            return storedInfo.Combine(sc =>
                                      (from f in sc.Fields
                                       where f.FieldName == fieldName &&
                                             f.TypeName == fieldtype.FullName
                                       select f).FirstMaybe())
                .Convert(f => f.IndexState)
                .GetValue(IndexingState.Unknown);
        }

        private ITypeDescription CreateType(IReflectClass classInfo,
                                            Func<TypeName, Maybe<IReflectClass>> classLookup,
                                            IDictionary<string, ITypeDescription> knownTypes)
        {
            var name = NameOf(classInfo);
            return name.ArrayOf.Convert(
                n => CreateArrayType(name, classInfo, classLookup, knownTypes))
                .GetValue(() =>
                          CreateType(name, classInfo, classLookup, knownTypes));
        }

        private ITypeDescription CreateType(TypeName name,
                                            IReflectClass classInfo,
                                            Func<TypeName, Maybe<IReflectClass>> classLookup,
                                            IDictionary<string, ITypeDescription> knownTypes)
        {
            var knownType = typeResolver(name)
                .Otherwise(() => typeResolver(name.GetGenericTypeDefinition()));
            if (knownType.HasValue)
            {
                var systemType = KnownType.Create(knownType.Value,
                                                  name.GenericArguments.Select(
                                                      t => GetOrCreateTypeByName(t.Value, classLookup, knownTypes)).
                                                      ToList(), IndexLookUp);
                knownTypes[name.FullName] = systemType;
                return systemType;
            }
            return SimpleClassDescription.Create(name,
                                                 classInfo.GetSuperclass().AsMaybe().Convert(sc=>GetOrCreateType(sc, classLookup,
                                                                            knownTypes)),
                                                 t =>
                                                     {
                                                         knownTypes[name.FullName] = t;
                                                         return ExtractFields(name, classInfo,
                                                                              typeName =>
                                                                              GetOrCreateType(typeName, classLookup,
                                                                                              knownTypes));
                                                     });
        }

        private ITypeDescription GetOrCreateTypeByName(TypeName name,
                                                       Func<TypeName, Maybe<IReflectClass>> classLookup,
                                                       IDictionary<string, ITypeDescription> knownTypes)
        {
            var type = knownTypes.TryGet(name.FullName);
            if(type.HasValue)
            {
                return type.Value;
            }
            var systemType = typeResolver(name);
            return systemType.Convert(KnownType.Create)
                .GetValue(()=>classLookup(name)
                    .Convert(n=>CreateType(name, n, classLookup, knownTypes))
                    .GetValue(()=>MockTypeFor(name,knownTypes)));
        }

        private ITypeDescription CreateArrayType(TypeName fullName,
                                                 IReflectClass classInfo, Func<TypeName, Maybe<IReflectClass>> classLookup,
                                                 IDictionary<string, ITypeDescription> knownTypes)
        {
            var innerType = GetOrCreateType(classInfo.GetComponentType(), classLookup, knownTypes);
            var type = ArrayDescription.Create(innerType, fullName.OrderOfArray);
            knownTypes[fullName.FullName] = type;
            return type;
        }

        private ITypeDescription GetOrCreateType(IReflectClass typeToFind, Func<TypeName, Maybe<IReflectClass>> classLookup,
                                                 IDictionary<string, ITypeDescription> knownTypes)
        {
            return knownTypes.TryGet(NameOf(typeToFind).FullName)
                .GetValue(() => CreateType(typeToFind, classLookup, knownTypes));
        }

        private static TypeName NameOf(IReflectClass typeToFind)
        {
            var name = TypeNameParser.ParseString(typeToFind.GetName());
            if (typeToFind.IsArray() && !name.ArrayOf.HasValue)
            {
                return TypeName.CreateArrayOf(name, 1);
            }
            return name;
        }


        private IEnumerable<SimpleFieldDescription> ExtractFields(TypeName declaringTypeName, IReflectClass classInfo,
                                                                  Func<IReflectClass, ITypeDescription> typeLookUp)
        {
            return classInfo.GetDeclaredFields()
                .Where(f=>!(f is GenericVirtualField))
                .Select(f => CreateField(declaringTypeName, f, typeLookUp));
        }

        private SimpleFieldDescription CreateField(TypeName declaredOn, IReflectField field,
                                                   Func<IReflectClass, ITypeDescription> typeLookUp)
        {
            var fieldType = typeLookUp(field.GetFieldType());
            return SimpleFieldDescription.Create(field.GetName(),
                                                 fieldType, IndexLookUp(declaredOn, field.GetName(), fieldType.TypeName));
        }


        private static IDictionary<string, CachedStoredClass> ExtractStoredClasses(IStoredClass[] storedClasses)
        {
            return (from storedClass in storedClasses
                    select new CachedStoredClass(storedClass.GetName(),
                                                 ExtractStoredFields(storedClass)))
                .ToDictionary(sc => sc.FullName, sc => sc);
        }

        private static IEnumerable<StoredFieldCache> ExtractStoredFields(IStoredClass storedClass)
        {
            return from sf in storedClass.GetStoredFields()
                   where sf.GetStoredType() != null
                   select new StoredFieldCache(sf.GetName(), sf.GetStoredType().GetName(), HasIndex(sf));
        }

        private static IndexingState HasIndex(IStoredField sf)
        {
            return sf.HasIndex() ? IndexingState.Indexed : IndexingState.NotIndexed;
        }

        /// <summary>
        ///   We cache this data to save time and avoid this db4o-bug  COR-2177
        /// </summary>
        private class CachedStoredClass
        {
            internal string FullName { get; private set; }
            internal IEnumerable<StoredFieldCache> Fields { get; private set; }

            public CachedStoredClass(string fullName, IEnumerable<StoredFieldCache> fields)
            {
                FullName = fullName;
                Fields = fields;
            }
        }

        private class StoredFieldCache
        {
            internal string TypeName { get; private set; }
            internal string FieldName { get; private set; }
            internal IndexingState IndexState { get; private set; }

            public StoredFieldCache(string fieldName, string typeName, IndexingState indexState)
            {
                TypeName = typeName;
                FieldName = fieldName;
                IndexState = indexState;
            }
        }
    }
}