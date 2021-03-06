﻿using System;
using System.Collections.Generic;
using Gamlor.Db4oPad.MetaInfo;
using Gamlor.Db4oPad.Tests.TestTypes;

namespace Gamlor.Db4oPad.Tests.MetaInfo
{
    public abstract class TypeGenerationBase
    {
        internal CodeGenerationResult NewTestInstance(IEnumerable<ITypeDescription> metaInfo)
        {
            return CodeGenerator.Create(metaInfo, TestUtils.NewName());
        }
        internal static IEnumerable<ITypeDescription> TypeWithGenericList()
        {
            var stringList = KnownType.Create(typeof(List<string>));
            var type = SimpleClassDescription.Create(TestMetaData.SingleFieldType(),
                                                     f => TestMetaData.CreateField(stringList));
            return new[] { type, stringList };
        }
        internal static IEnumerable<ITypeDescription> GenericType()
        {
            var stringList = KnownType.Create(typeof(List<string>));
            var stringType = typeof(string);
            var genericArguments = GenericArg(stringType);
            var type = SimpleClassDescription.Create(TypeName.Create(TestMetaData.SingleFieldTypeName+"`1",
                TestMetaData.AssemblyName, genericArguments),
                                                     f => TestMetaData.CreateField(stringList));
            return new[] { type, stringList };
        }

        internal static IEnumerable<TypeName> GenericArg(Type intType)
        {
            return new[] { TypeName.Create(intType.FullName, intType.Assembly.GetName().Name) };
        }

    }
}