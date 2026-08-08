using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MetaCompanion;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class LogTest
	{
		[TestMethod]
		public void Info_AddedToLogQueue()
		{
			Log.Info("测试信息已写入。");
			Assert.IsTrue(Log.PrevLine.Contains("测试信息已写入。"));
		}

	}
}
