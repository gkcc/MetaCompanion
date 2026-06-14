using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class HdtConfigMutationTest
	{
		private static readonly OpCode[] SingleByteOpCodes = new OpCode[0x100];
		private static readonly OpCode[] MultiByteOpCodes = new OpCode[0x100];

		static HdtConfigMutationTest()
		{
			foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
			{
				var opCode = (OpCode)field.GetValue(null);
				var value = unchecked((ushort)opCode.Value);
				if (value < 0x100)
				{
					SingleByteOpCodes[value] = opCode;
				}
				else if ((value & 0xff00) == 0xfe00)
				{
					MultiByteOpCodes[value & 0xff] = opCode;
				}
			}
		}

		[TestMethod]
		public void Plugin_DoesNotMutateHdtGlobalConfig()
		{
			var calls = FindHdtConfigSetterCalls(typeof(MetaCompanionPlugin).Assembly)
				.ToList();

			Assert.AreEqual(
				0,
				calls.Count,
				"Meta Companion must not write Hearthstone Deck Tracker global Config. Calls: " +
					string.Join(", ", calls));
		}

		private static IEnumerable<string> FindHdtConfigSetterCalls(Assembly assembly)
		{
			foreach (var type in assembly.GetTypes())
			{
				foreach (var method in type.GetMethods(
					BindingFlags.Public |
					BindingFlags.NonPublic |
					BindingFlags.Instance |
					BindingFlags.Static |
					BindingFlags.DeclaredOnly))
				{
					foreach (var setter in FindHdtConfigSetterCalls(method))
					{
						yield return type.FullName + "." + method.Name + " -> " + setter;
					}
				}
			}
		}

		private static IEnumerable<string> FindHdtConfigSetterCalls(MethodInfo method)
		{
			var body = method.GetMethodBody();
			if (body == null)
			{
				yield break;
			}

			var bytes = body.GetILAsByteArray();
			var position = 0;
			while (position < bytes.Length)
			{
				var opCode = ReadOpCode(bytes, ref position);
				if (opCode.OperandType == OperandType.InlineMethod ||
					opCode.OperandType == OperandType.InlineTok)
				{
					var token = BitConverter.ToInt32(bytes, position);
					var member = ResolveMember(method.Module, token);
					var calledMethod = member as MethodBase;
					if (calledMethod != null &&
						calledMethod.Name.StartsWith("set_", StringComparison.Ordinal) &&
						calledMethod.DeclaringType != null &&
						calledMethod.DeclaringType.FullName == "Hearthstone_Deck_Tracker.Config")
					{
						yield return calledMethod.DeclaringType.FullName + "." + calledMethod.Name;
					}
				}

				position += GetOperandSize(bytes, position, opCode.OperandType);
			}
		}

		private static OpCode ReadOpCode(byte[] bytes, ref int position)
		{
			var first = bytes[position++];
			if (first != 0xfe)
			{
				return SingleByteOpCodes[first];
			}

			return MultiByteOpCodes[bytes[position++]];
		}

		private static MemberInfo ResolveMember(Module module, int metadataToken)
		{
			try
			{
				return module.ResolveMember(metadataToken);
			}
			catch (ArgumentException)
			{
				return null;
			}
		}

		private static int GetOperandSize(byte[] bytes, int position, OperandType operandType)
		{
			switch (operandType)
			{
				case OperandType.InlineNone:
					return 0;
				case OperandType.ShortInlineBrTarget:
				case OperandType.ShortInlineI:
				case OperandType.ShortInlineVar:
					return 1;
				case OperandType.InlineVar:
					return 2;
				case OperandType.InlineBrTarget:
				case OperandType.InlineField:
				case OperandType.InlineI:
				case OperandType.InlineMethod:
				case OperandType.InlineSig:
				case OperandType.InlineString:
				case OperandType.InlineTok:
				case OperandType.InlineType:
				case OperandType.ShortInlineR:
					return 4;
				case OperandType.InlineSwitch:
					var count = BitConverter.ToInt32(bytes, position);
					return 4 + 4 * count;
				case OperandType.InlineI8:
				case OperandType.InlineR:
					return 8;
				default:
					throw new NotSupportedException("Unsupported operand type: " + operandType);
			}
		}
	}
}
