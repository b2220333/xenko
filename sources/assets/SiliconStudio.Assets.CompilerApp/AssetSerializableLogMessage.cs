﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.
using System;

using SiliconStudio.Assets.Diagnostics;
using SiliconStudio.Core;
using SiliconStudio.Core.Diagnostics;
using SiliconStudio.Core.IO;

namespace SiliconStudio.Assets.CompilerApp
{
    [DataContract]
    public class AssetSerializableLogMessage : SerializableLogMessage
    {
        public AssetSerializableLogMessage()
        {

        }

        public AssetSerializableLogMessage(AssetLogMessage logMessage)
            : base(logMessage)
        {
            if (logMessage.AssetReference != null)
            {
                AssetId = logMessage.AssetReference.Id;
                AssetUrl = logMessage.AssetReference.Location;
            }
        }

        public AssetSerializableLogMessage(Guid assetId, UFile assetUrl, LogMessageType type, string text, ExceptionInfo exceptionInfo = null)
            : base("", type, text, exceptionInfo)
        {
            AssetId = assetId;
            AssetUrl = assetUrl;
        }

        public Guid AssetId { get; set; }

        public UFile AssetUrl { get; set; }
    }
}