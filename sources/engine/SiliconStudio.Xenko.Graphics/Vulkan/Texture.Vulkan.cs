// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.

#if SILICONSTUDIO_XENKO_GRAPHICS_API_VULKAN
// Copyright (c) 2010-2012 SharpDX - Alexandre Mutel
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Linq;
using SharpVulkan;
using SiliconStudio.Core;
using SiliconStudio.Core.Mathematics;

namespace SiliconStudio.Xenko.Graphics
{
    public partial class Texture
    {
        internal const int TextureSubresourceAlignment = 4;
        internal const int TextureRowPitchAlignment = 1;
        internal int TexturePixelSize;

        internal SharpVulkan.Image NativeImage;
        internal SharpVulkan.Buffer NativeBuffer;
        internal ImageView NativeColorAttachmentView;
        internal ImageView NativeDepthStencilView;
        internal ImageView NativeImageView;

        private bool isNotOwningResources;
        internal bool IsInitialized;

        internal Format NativeFormat;
        internal bool HasStencil;

        internal ImageLayout NativeLayout;
        internal AccessFlags NativeAccessMask;
        internal ImageAspectFlags NativeImageAspect;

        public void Recreate(DataBox[] dataBoxes = null)
        {
            InitializeFromImpl(dataBoxes);
        }

        public static bool IsDepthStencilReadOnlySupported(GraphicsDevice device)
        {
            // TODO VULKAN
            return true;
        }

        internal Texture InitializeFromPersistent(TextureDescription description, SharpVulkan.Image nativeImage)
        {
            NativeImage = nativeImage;

            return InitializeFrom(description);
        }

        internal Texture InitializeWithoutResources(TextureDescription description)
        {
            isNotOwningResources = true;
            return InitializeFrom(description);
        }

        internal void SetNativeHandles(SharpVulkan.Image image, ImageView attachmentView)
        {
            NativeImage = image;
            NativeColorAttachmentView = attachmentView;
        }

        private void InitializeFromImpl(DataBox[] dataBoxes = null)
        {
            bool isCompressed;
            VulkanConvertExtensions.ConvertPixelFormat(ViewFormat, out NativeFormat, out TexturePixelSize, out isCompressed);
            HasStencil = IsStencilFormat(ViewFormat);
            
            NativeImageAspect = IsDepthStencil ? ImageAspectFlags.Depth : ImageAspectFlags.Color;
            if (HasStencil)
                NativeImageAspect |= ImageAspectFlags.Stencil;

            // For depth-stencil formats, automatically fall back to a supported one
            if (IsDepthStencil && HasStencil)
            {
                NativeFormat = GetFallbackDepthStencilFormat(GraphicsDevice, NativeFormat);
            }

            if (Usage == GraphicsResourceUsage.Staging)
            {
                if (NativeImage != SharpVulkan.Image.Null)
                    throw new InvalidOperationException();

                if (isNotOwningResources)
                    throw new InvalidOperationException();

                if (ParentTexture != null)
                {
                    // Create only a view
                    NativeBuffer = ParentTexture.NativeBuffer;
                    NativeMemory = ParentTexture.NativeMemory;
                }
                else
                {
                    CreateBuffer();

                    if (dataBoxes != null)
                        throw new InvalidOperationException();
                }
            }
            else
            {
                if (NativeImage != SharpVulkan.Image.Null)
                    throw new InvalidOperationException();

                NativeLayout =
                    IsRenderTarget ? ImageLayout.ColorAttachmentOptimal :
                    IsDepthStencil ? ImageLayout.DepthStencilAttachmentOptimal :
                    IsShaderResource ? ImageLayout.ShaderReadOnlyOptimal :
                    ImageLayout.General;

                if (NativeLayout == ImageLayout.TransferDestinationOptimal)
                    NativeAccessMask = AccessFlags.TransferRead;

                if (NativeLayout == ImageLayout.ColorAttachmentOptimal)
                    NativeAccessMask = AccessFlags.ColorAttachmentWrite;

                if (NativeLayout == ImageLayout.DepthStencilAttachmentOptimal)
                    NativeAccessMask = AccessFlags.DepthStencilAttachmentWrite;

                if (NativeLayout == ImageLayout.ShaderReadOnlyOptimal)
                    NativeAccessMask = AccessFlags.ShaderRead | AccessFlags.InputAttachmentRead;

                if (ParentTexture != null)
                {
                    // Create only a view
                    NativeImage = ParentTexture.NativeImage;
                    NativeMemory = ParentTexture.NativeMemory;
                }
                else
                {
                    if (!isNotOwningResources)
                    {
                        CreateImage();

                        InitializeImage(dataBoxes);
                    }
                }

                if (!isNotOwningResources)
                {
                    NativeImageView = GetImageView(ViewType, ArraySlice, MipLevel);
                    NativeColorAttachmentView = GetColorAttachmentView(ViewType, ArraySlice, MipLevel);
                    NativeDepthStencilView = GetDepthStencilView();
                }
            }
        }

        private unsafe void CreateBuffer()
        {
            var createInfo = new BufferCreateInfo
            {
                StructureType = StructureType.BufferCreateInfo,
                Flags = BufferCreateFlags.None
            };

            for (int i = 0; i < MipLevels; i++)
            { 
                var mipmap = GetMipMapDescription(i);
                createInfo.Size += (uint)(mipmap.DepthStride * mipmap.Depth * ArraySize);
            }

            createInfo.Usage = BufferUsageFlags.TransferSource | BufferUsageFlags.TransferDestination;

            // Create buffer
            NativeBuffer = GraphicsDevice.NativeDevice.CreateBuffer(ref createInfo);

            // Allocate and bind memory
            MemoryRequirements memoryRequirements;
            GraphicsDevice.NativeDevice.GetBufferMemoryRequirements(NativeBuffer, out memoryRequirements);

            AllocateMemory(MemoryPropertyFlags.HostVisible | MemoryPropertyFlags.HostCoherent, memoryRequirements);

            if (NativeMemory != DeviceMemory.Null)
            {
                GraphicsDevice.NativeDevice.BindBufferMemory(NativeBuffer, NativeMemory, 0);
            }
        }

        private unsafe void CreateImage()
        {
            // Create a new image
            var createInfo = new ImageCreateInfo
            {
                StructureType = StructureType.ImageCreateInfo,
                ArrayLayers = (uint)ArraySize,
                Extent = new Extent3D((uint)Width, (uint)Height, (uint)Depth),
                MipLevels = (uint)MipLevels,
                Samples = SampleCountFlags.Sample1,
                Format = NativeFormat,
                Flags = ImageCreateFlags.None,
                Tiling = ImageTiling.Optimal,
                InitialLayout = ImageLayout.Undefined
            };

            switch (Dimension)
            {
                case TextureDimension.Texture1D:
                    createInfo.ImageType = ImageType.Image1D;
                    break;
                case TextureDimension.Texture2D:
                    createInfo.ImageType = ImageType.Image2D;
                    break;
                case TextureDimension.Texture3D:
                    createInfo.ImageType = ImageType.Image3D;
                    break;
                case TextureDimension.TextureCube:
                    createInfo.ImageType = ImageType.Image2D;
                    createInfo.Flags |= ImageCreateFlags.CubeCompatible;
                    break;
            }

            // TODO VULKAN: Can we restrict more based on GraphicsResourceUsage? 
            createInfo.Usage |= ImageUsageFlags.TransferSource | ImageUsageFlags.TransferDestination;

            if (IsRenderTarget)
                createInfo.Usage |= ImageUsageFlags.ColorAttachment;

            if (IsDepthStencil)
                createInfo.Usage |= ImageUsageFlags.DepthStencilAttachment;

            if (IsShaderResource)
                createInfo.Usage |= ImageUsageFlags.Sampled; // TODO VULKAN: Input attachments

            var memoryProperties = MemoryPropertyFlags.DeviceLocal;

            // Create native image
            // TODO: Multisampling, flags, usage, etc.
            NativeImage = GraphicsDevice.NativeDevice.CreateImage(ref createInfo);

            // Allocate and bind memory
            MemoryRequirements memoryRequirements;
            GraphicsDevice.NativeDevice.GetImageMemoryRequirements(NativeImage, out memoryRequirements);

            AllocateMemory(memoryProperties, memoryRequirements);

            if (NativeMemory != DeviceMemory.Null)
            {
                GraphicsDevice.NativeDevice.BindImageMemory(NativeImage, NativeMemory, 0);
            }
        }

        private unsafe void InitializeImage(DataBox[] dataBoxes)
        {
            var commandBuffer = GraphicsDevice.NativeCopyCommandBuffer;
            var beginInfo = new CommandBufferBeginInfo { StructureType = StructureType.CommandBufferBeginInfo };
            commandBuffer.Begin(ref beginInfo);

            if (dataBoxes != null && dataBoxes.Length > 0)
            {
                int totalSize = dataBoxes.Length * 4;
                for (int i = 0; i < dataBoxes.Length; i++)
                {
                    totalSize += dataBoxes[i].SlicePitch;
                }

                SharpVulkan.Buffer uploadResource;
                int uploadOffset;
                var uploadMemory = GraphicsDevice.AllocateUploadBuffer(totalSize, out uploadResource, out uploadOffset);

                // Upload buffer barrier
                var bufferMemoryBarrier = new BufferMemoryBarrier(uploadResource, AccessFlags.HostWrite, AccessFlags.TransferRead, (ulong)uploadOffset, (ulong)totalSize);

                // Image barrier
                var initialBarrier = new ImageMemoryBarrier(NativeImage, ImageLayout.Undefined, ImageLayout.TransferDestinationOptimal, AccessFlags.None, AccessFlags.TransferWrite, new ImageSubresourceRange(NativeImageAspect));
                commandBuffer.PipelineBarrier(PipelineStageFlags.TopOfPipe, PipelineStageFlags.Transfer, DependencyFlags.None, 0, null, 1, &bufferMemoryBarrier, 1, &initialBarrier);

                // Copy data boxes to upload buffer
                var copies = new BufferImageCopy[dataBoxes.Length];
                for (int i = 0; i < copies.Length; i++)
                {
                    var slicePitch = dataBoxes[i].SlicePitch;

                    int arraySlice = i / MipLevels;
                    int mipSlice = i % MipLevels;
                    var mipMapDescription = GetMipMapDescription(mipSlice);

                    SubresourceLayout layout;
                    GraphicsDevice.NativeDevice.GetImageSubresourceLayout(NativeImage, new ImageSubresource(NativeImageAspect, (uint)arraySlice, (uint)mipSlice), out layout);

                    var alignment = ((uploadOffset + 3) & ~3) - uploadOffset;
                    uploadMemory += alignment;
                    uploadOffset += alignment;

                    Utilities.CopyMemory(uploadMemory, dataBoxes[i].DataPointer, slicePitch);

                    // TODO VULKAN: Check if pitches are valid
                    copies[i] = new BufferImageCopy
                    {
                        BufferOffset = (ulong)uploadOffset,
                        ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.Color, (uint)arraySlice, 1, (uint)mipSlice),
                        BufferRowLength = 0, //(uint)(dataBoxes[i].RowPitch / pixelSize),
                        BufferImageHeight = 0, //(uint)(dataBoxes[i].SlicePitch / dataBoxes[i].RowPitch),
                        ImageOffset = new Offset3D(0, 0, arraySlice),
                        ImageExtent = new Extent3D((uint)mipMapDescription.Width, (uint)mipMapDescription.Height, 1)
                    };

                    uploadMemory += slicePitch;
                    uploadOffset += slicePitch;
                }

                // Copy from upload buffer to image
                fixed (BufferImageCopy* copiesPointer = &copies[0])
                {
                    commandBuffer.CopyBufferToImage(uploadResource, NativeImage, ImageLayout.TransferDestinationOptimal, (uint)copies.Length, copiesPointer);
                }

                IsInitialized = true;
            }

            // Transition to default layout
            var imageMemoryBarrier = new ImageMemoryBarrier(NativeImage,
                dataBoxes == null || dataBoxes.Length == 0 ? ImageLayout.Undefined : ImageLayout.TransferDestinationOptimal, NativeLayout,
                dataBoxes == null || dataBoxes.Length == 0 ? AccessFlags.None : AccessFlags.TransferWrite, NativeAccessMask, new ImageSubresourceRange(NativeImageAspect));
            commandBuffer.PipelineBarrier(PipelineStageFlags.Transfer, PipelineStageFlags.AllCommands, DependencyFlags.None, 0, null, 0, null, 1, &imageMemoryBarrier);

            // Close and submit
            commandBuffer.End();

            var submitInfo = new SubmitInfo
            {
                StructureType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                CommandBuffers = new IntPtr(&commandBuffer),
            };

            GraphicsDevice.NativeCommandQueue.Submit(1, &submitInfo, Fence.Null);
            GraphicsDevice.NativeCommandQueue.WaitIdle();
            commandBuffer.Reset(CommandBufferResetFlags.None);
        }

        /// <inheritdoc/>
        protected internal override unsafe void OnDestroyed()
        {
            if (ParentTexture != null || isNotOwningResources)
            {
                NativeImage = SharpVulkan.Image.Null;
                NativeMemory = DeviceMemory.Null;
            }

            if (!isNotOwningResources)
            {
                if (NativeMemory != DeviceMemory.Null)
                {
                    GraphicsDevice.NativeDevice.FreeMemory(NativeMemory);
                    NativeMemory = DeviceMemory.Null;
                }

                if (NativeImage != SharpVulkan.Image.Null)
                {
                    GraphicsDevice.NativeDevice.DestroyImage(NativeImage);
                    NativeImage = SharpVulkan.Image.Null;
                }

                if (NativeBuffer != SharpVulkan.Buffer.Null)
                {
                    GraphicsDevice.NativeDevice.DestroyBuffer(NativeBuffer);
                    NativeBuffer = SharpVulkan.Buffer.Null;
                }

                if (NativeImageView != ImageView.Null)
                {
                    GraphicsDevice.NativeDevice.DestroyImageView(NativeImageView);
                    NativeImageView = ImageView.Null;
                }

                if (NativeColorAttachmentView != ImageView.Null)
                {
                    GraphicsDevice.NativeDevice.DestroyImageView(NativeColorAttachmentView);
                    NativeColorAttachmentView = ImageView.Null;
                }

                if (NativeDepthStencilView != ImageView.Null)
                {
                    GraphicsDevice.NativeDevice.DestroyImageView(NativeDepthStencilView);
                    NativeDepthStencilView = ImageView.Null;
                }
            }

            base.OnDestroyed();
        }

        private void OnRecreateImpl()
        {
            // Dependency: wait for underlying texture to be recreated
            if (ParentTexture != null && ParentTexture.LifetimeState != GraphicsResourceLifetimeState.Active)
                return;

            // Render Target / Depth Stencil are considered as "dynamic"
            if ((Usage == GraphicsResourceUsage.Immutable
                    || Usage == GraphicsResourceUsage.Default)
                && !IsRenderTarget && !IsDepthStencil)
                return;

            if (ParentTexture == null && GraphicsDevice != null)
            {
                GraphicsDevice.TextureMemory -= (Depth * DepthStride) / (float)0x100000;
            }

            InitializeFromImpl();
        }

        private unsafe ImageView GetImageView(ViewType viewType, int arrayOrDepthSlice, int mipIndex)
        {
            if (!IsShaderResource)
                return ImageView.Null;

            if (viewType == ViewType.MipBand)
                throw new NotSupportedException("ViewSlice.MipBand is not supported for render targets");

            int arrayOrDepthCount;
            int mipCount;
            GetViewSliceBounds(viewType, ref arrayOrDepthSlice, ref mipIndex, out arrayOrDepthCount, out mipCount);

            var layerCount = Dimension == TextureDimension.Texture3D ? 1 : arrayOrDepthCount;

            var createInfo = new ImageViewCreateInfo
            {
                StructureType = StructureType.ImageViewCreateInfo,
                Format = NativeFormat, //VulkanConvertExtensions.ConvertPixelFormat(ViewFormat),
                Image = NativeImage,
                Components = ComponentMapping.Identity,
                SubresourceRange = new ImageSubresourceRange(IsDepthStencil ? ImageAspectFlags.Depth : ImageAspectFlags.Color, (uint)arrayOrDepthSlice, (uint)layerCount, (uint)mipIndex, (uint)mipCount)
            };

            if (IsMultiSample)
                throw new NotImplementedException();

            if (this.ArraySize > 1)
            {
                if (IsMultiSample && Dimension != TextureDimension.Texture2D)
                    throw new NotSupportedException("Multisample is only supported for 2D Textures");

                if (Dimension == TextureDimension.Texture3D)
                    throw new NotSupportedException("Texture Array is not supported for Texture3D");

                switch (Dimension)
                {
                    case TextureDimension.Texture1D:
                        createInfo.ViewType = ImageViewType.Image1DArray;
                        break;
                    case TextureDimension.Texture2D:
                        createInfo.ViewType = ImageViewType.Image2DArray;
                        break;
                    case TextureDimension.TextureCube:
                        createInfo.ViewType = ImageViewType.ImageCubeArray;
                        break;
                }
            }
            else
            {
                if (IsMultiSample && Dimension != TextureDimension.Texture2D)
                    throw new NotSupportedException("Multisample is only supported for 2D RenderTarget Textures");

                if (Dimension == TextureDimension.TextureCube)
                    throw new NotSupportedException("TextureCube dimension is expecting an arraysize > 1");

                switch (Dimension)
                {
                    case TextureDimension.Texture1D:
                        createInfo.ViewType = ImageViewType.Image1D;
                        break;
                    case TextureDimension.Texture2D:
                        createInfo.ViewType = ImageViewType.Image2D;
                        break;
                    case TextureDimension.Texture3D:
                        createInfo.ViewType = ImageViewType.Image3D;
                        break;
                    case TextureDimension.TextureCube:
                        createInfo.ViewType = ImageViewType.ImageCube;
                        break;
                }
            }

            return GraphicsDevice.NativeDevice.CreateImageView(ref createInfo);
        }

        private unsafe ImageView GetColorAttachmentView(ViewType viewType, int arrayOrDepthSlice, int mipIndex)
        {
            if (!IsRenderTarget)
                return ImageView.Null;

            if (viewType == ViewType.MipBand)
                throw new NotSupportedException("ViewSlice.MipBand is not supported for render targets");

            int arrayOrDepthCount;
            int mipCount;
            GetViewSliceBounds(viewType, ref arrayOrDepthSlice, ref mipIndex, out arrayOrDepthCount, out mipCount);

            var createInfo = new ImageViewCreateInfo
            {
                StructureType = StructureType.ImageViewCreateInfo,
                ViewType = ImageViewType.Image2D,
                Format = NativeFormat, // VulkanConvertExtensions.ConvertPixelFormat(ViewFormat),
                Image = NativeImage,
                Components = ComponentMapping.Identity,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.Color, (uint)arrayOrDepthSlice, 1, (uint)mipIndex, (uint)mipCount)
            };

            if (IsMultiSample)
                throw new NotImplementedException();

            if (this.ArraySize > 1)
            {
                if (IsMultiSample && Dimension != TextureDimension.Texture2D)
                    throw new NotSupportedException("Multisample is only supported for 2D Textures");

                if (Dimension == TextureDimension.Texture3D)
                    throw new NotSupportedException("Texture Array is not supported for Texture3D");
            }
            else
            {
                if (IsMultiSample && Dimension != TextureDimension.Texture2D)
                    throw new NotSupportedException("Multisample is only supported for 2D RenderTarget Textures");

                if (Dimension == TextureDimension.TextureCube)
                    throw new NotSupportedException("TextureCube dimension is expecting an arraysize > 1");
            }

            return GraphicsDevice.NativeDevice.CreateImageView(ref createInfo);
        }

        private unsafe ImageView GetDepthStencilView()
        {
            if (!IsDepthStencil)
                return ImageView.Null;

            // Check that the format is supported
            //if (ComputeShaderResourceFormatFromDepthFormat(ViewFormat) == PixelFormat.None)
            //    throw new NotSupportedException("Depth stencil format [{0}] not supported".ToFormat(ViewFormat));

            // Create a Depth stencil view on this texture2D
            var createInfo = new ImageViewCreateInfo
            {
                StructureType = StructureType.ImageViewCreateInfo,
                ViewType = ImageViewType.Image2D,
                Format = NativeFormat, //VulkanConvertExtensions.ConvertPixelFormat(ViewFormat),
                Image = NativeImage,
                Components = ComponentMapping.Identity,
                SubresourceRange = new ImageSubresourceRange(NativeImageAspect, 0, 1, 0, 1)
            };

            //if (IsDepthStencilReadOnly)
            //{
            //    if (!IsDepthStencilReadOnlySupported(GraphicsDevice))
            //        throw new NotSupportedException("Cannot instantiate ReadOnly DepthStencilBuffer. Not supported on this device.");

            //    // Create a Depth stencil view on this texture2D
            //    createInfo.SubresourceRange.AspectMask =  ? ;
            //    if (HasStencil)
            //        createInfo.Flags |= (int)AttachmentViewCreateFlags.AttachmentViewCreateReadOnlyStencilBit;
            //}

            return GraphicsDevice.NativeDevice.CreateImageView(ref createInfo);
        }

        private bool IsFlipped()
        {
            return false;
        }

        internal static PixelFormat ComputeShaderResourceFormatFromDepthFormat(PixelFormat format)
        {
            return format;
        }

        /// <summary>
        /// Check and modify if necessary the mipmap levels of the image (Troubles with DXT images whose resolution in less than 4x4 in DX9.x).
        /// </summary>
        /// <param name="device">The graphics device.</param>
        /// <param name="description">The texture description.</param>
        /// <returns>The updated texture description.</returns>
        private static TextureDescription CheckMipLevels(GraphicsDevice device, ref TextureDescription description)
        {
            if (device.Features.CurrentProfile < GraphicsProfile.Level_10_0 && (description.Flags & TextureFlags.DepthStencil) == 0 && description.Format.IsCompressed())
            {
                description.MipLevels = Math.Min(CalculateMipCount(description.Width, description.Height), description.MipLevels);
            }
            return description;
        }

        /// <summary>
        /// Calculates the mip level from a specified size.
        /// </summary>
        /// <param name="size">The size.</param>
        /// <param name="minimumSizeLastMip">The minimum size of the last mip.</param>
        /// <returns>The mip level.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException">Value must be > 0;size</exception>
        private static int CalculateMipCountFromSize(int size, int minimumSizeLastMip = 4)
        {
            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException("Value must be > 0", "size");
            }

            if (minimumSizeLastMip <= 0)
            {
                throw new ArgumentOutOfRangeException("Value must be > 0", "minimumSizeLastMip");
            }

            int level = 1;
            while ((size / 2) >= minimumSizeLastMip)
            {
                size = Math.Max(1, size / 2);
                level++;
            }
            return level;
        }

        /// <summary>
        /// Calculates the mip level from a specified width,height,depth.
        /// </summary>
        /// <param name="width">The width.</param>
        /// <param name="height">The height.</param>
        /// <param name="minimumSizeLastMip">The minimum size of the last mip.</param>
        /// <returns>The mip level.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException">Value must be &gt; 0;size</exception>
        private static int CalculateMipCount(int width, int height, int minimumSizeLastMip = 4)
        {
            return Math.Min(CalculateMipCountFromSize(width, minimumSizeLastMip), CalculateMipCountFromSize(height, minimumSizeLastMip));
        }

        internal static Format GetFallbackDepthStencilFormat(GraphicsDevice device, Format format)
        {
            if (format == SharpVulkan.Format.D16UNormS8UInt || format == SharpVulkan.Format.D24UNormS8UInt || format == SharpVulkan.Format.D32SFloatS8UInt)
            {
                var fallbackFormats = new[] { format, SharpVulkan.Format.D32SFloatS8UInt, SharpVulkan.Format.D24UNormS8UInt, SharpVulkan.Format.D16UNormS8UInt };

                foreach (var fallbackFormat in fallbackFormats)
                {
                    FormatProperties formatProperties;
                    device.Adapter.PhysicalDevice.GetFormatProperties(fallbackFormat, out formatProperties);

                    if ((formatProperties.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachment) != 0)
                    {
                        format = fallbackFormat;
                        break;
                    }
                }
            }

            return format;
        }

        internal static bool IsStencilFormat(PixelFormat format)
        {
            switch (format)
            {
                case PixelFormat.R24G8_Typeless:
                case PixelFormat.D24_UNorm_S8_UInt:
                case PixelFormat.R32G8X24_Typeless:
                case PixelFormat.D32_Float_S8X24_UInt:
                    return true;
            }

            return false;
        }
    }
}
#endif