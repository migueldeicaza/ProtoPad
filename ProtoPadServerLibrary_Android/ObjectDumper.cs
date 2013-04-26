﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Android.Graphics;
using Android.Widget;
using ServiceDiscovery;

namespace ProtoPadServerLibrary_Android
{
    public class Dumper
    {
        /// <param name="primitiveValue"> must be a value type!!</param>
        public static DumpValue AsPrimitiveValue(object primitiveValue)
        {
            return new DumpValue { TypeName = primitiveValue.GetType().Name, DumpType = DumpValue.DumpTypes.Primitive, PrimitiveValue = primitiveValue };
        }

        /// <param name="primitiveEnumerable"> must be a list of value type!!</param>
		public static DumpValue AsPrimitiveEnumerable(IEnumerable<object> primitiveEnumerable, string typeName)
        {
            return new DumpValue { TypeName = typeName, DumpType = DumpValue.DumpTypes.PrimitiveEnumerable, PrimitiveEnumerable = primitiveEnumerable.ToList () };
        }

        public static DumpValue AsComplexValue(string typeName)
        {
            return new DumpValue { TypeName = typeName, DumpType = DumpValue.DumpTypes.Complex, ComplexValue = new Dictionary<string, DumpValue>() };
        }

		public static DumpValue AsComplexEnumerable(IEnumerable<DumpValue> complexEnumerable, string typeName)
        {
            return new DumpValue { TypeName = typeName, DumpType = DumpValue.DumpTypes.ComplexEnumerable, ComplexEnumerable = complexEnumerable.ToList () };
        }

        public static DumpValue AsBeyondMaxLevel(string typeName)
        {
            return new DumpValue { TypeName = typeName, DumpType = DumpValue.DumpTypes.BeyondMaxLevel };
        }

        public static DumpValue AsImage(Bitmap image)
        {
            byte[] dataBytes;
            using (var stream = new MemoryStream())
            {
                image.Compress(Bitmap.CompressFormat.Jpeg, 60, stream);
                dataBytes = stream.ToArray();
            }
            return new DumpValue { TypeName = "___IMAGE___", DumpType = DumpValue.DumpTypes.Image, PrimitiveValue = Convert.ToBase64String(dataBytes) };
        }

        public static DumpValue ObjectToDumpValue(object viewModel, int maxDepth = 2, int maxEnumerableItemCount = 1000)
        {
            if (viewModel is Bitmap)
            {
                return AsImage(viewModel as Bitmap);
            } 
            if (viewModel is ImageView)
            {
                var imageView = viewModel as ImageView;
                imageView.BuildDrawingCache();
                return AsImage(imageView.GetDrawingCache(true));
            }
            return DumpObjectRecursive(viewModel, maxDepth, 0, maxEnumerableItemCount);
        }

		private static string lastFieldName = null;

        private static DumpValue DumpObjectRecursive(object sourceValue, int maxDepth, int currentDepth, int maxEnumerableItemCount)
        {
            if (sourceValue == null) return null;
            var modelType = sourceValue.GetType();

            if (modelType.IsValueType || modelType == typeof(string))
            {
                if (sourceValue is IntPtr) return null; // todo: pointers altijd negeren?
                return AsPrimitiveValue(sourceValue);
            }

            if (currentDepth > maxDepth) return AsBeyondMaxLevel(modelType.Name);

            var isGenericEnumerable = false;
            try
            {
                isGenericEnumerable = modelType.GetInterfaces().Where(i => i.IsGenericType).Select(i => i.GetGenericTypeDefinition())
                    .Any(i => i == typeof(IEnumerable<>));
            }
            catch { }
            if (isGenericEnumerable)
            {
				var items = sourceValue as IEnumerable;
				if (items != null)
				{
					var valueList = new List<DumpValue>();
					int i = 0;
					var enumerator = items.GetEnumerator();
					object item = null;
					if (enumerator != null)
					{
						bool hasItems;
						try
						{
							// a bug in Xamarin.Android causes Application.SharedLibraries to contain a fake enumerable, which will throw an exception if enumerated.
							// we work around this for now by using a safe initial enumeration attempt
							hasItems = enumerator.MoveNext();
						} catch
						{
							hasItems = false;
						}

						while (hasItems)
						{
							item = enumerator.Current;
							if (i >= maxEnumerableItemCount) break;
							var processedItem = DumpObjectRecursive(item, maxDepth, currentDepth + 1, maxEnumerableItemCount);
							if (processedItem != null) valueList.Add (processedItem);
							i++;
							hasItems = enumerator.MoveNext();
						}
						if (valueList.Any())
						{
							var firstValue = valueList.First();
							return firstValue.DumpType == DumpValue.DumpTypes.Primitive
								? AsPrimitiveEnumerable(valueList.Select(v => v.PrimitiveValue), modelType.Name)
									: AsComplexEnumerable(valueList, modelType.Name);
						}
					}
				}
            }
            else if (modelType == typeof(IEnumerable))
            {
                var items = (IEnumerable)sourceValue;
                return AsComplexEnumerable(items.Cast<object>().Take(maxEnumerableItemCount).Select(v => DumpObjectRecursive(v, maxDepth, currentDepth + 1, maxEnumerableItemCount)).ToList(), modelType.Name);
            }

            var complexValue = AsComplexValue(modelType.Name);

            var fields = modelType.GetFields(BindingFlags.Instance | BindingFlags.Public);
            foreach (var field in fields)
            {
                ProcessFieldOrProperty(sourceValue, maxDepth, currentDepth, field, null, complexValue, maxEnumerableItemCount);
            }
            var properties = modelType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            foreach (var property in properties)
            {
                ProcessFieldOrProperty(sourceValue, maxDepth, currentDepth, null, property, complexValue, maxEnumerableItemCount);
            }

            return complexValue.ComplexValue.Any() ? complexValue : null;
        }

        private static void ProcessFieldOrProperty(object sourceValue, int maxDepth, int currentDepth, FieldInfo field, PropertyInfo property, DumpValue complexValue, int maxEnumerableItemCount)
        {
            object fieldValue;
            try
            {
                fieldValue = property == null ? field.GetValue(sourceValue) : property.GetValue(sourceValue, null);
            }
            catch
            {
                return;
            }
            var fieldName = property == null ? field.Name : property.Name;
			lastFieldName = fieldName;
            if (fieldValue == null) return;

            var value = DumpObjectRecursive(fieldValue, maxDepth, currentDepth + 1, maxEnumerableItemCount);
            if (value != null) complexValue.AddComplexFieldValue(fieldName, value);
        }
    }
}