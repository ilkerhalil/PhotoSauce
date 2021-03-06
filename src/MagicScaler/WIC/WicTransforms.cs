﻿using System;
using System.Linq;
using System.Buffers;
using System.Drawing;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using PhotoSauce.MagicScaler.Interop;

namespace PhotoSauce.MagicScaler
{
	internal class WicFrameReader
	{
		public IWICBitmapFrameDecode Frame { get; private set; }
		public double DpiX { get; private set; }
		public double DpiY { get; private set; }
		public bool SupportsNativeScale { get; private set; }
		public bool SupportsNativeTransform { get; private set; }
		public bool SupportsPlanarPipeline { get; set; }
		public Orientation ExifOrientation { get; set; } = Orientation.Normal;
		public IDictionary<string, PropVariant> Metadata { get; set; }

		public WicFrameReader(WicProcessingContext ctx, bool planar = false)
		{
			ctx.DecoderFrame = this;

			if(ctx.Decoder.Decoder == null)
			{
				DpiX = DpiY = 96d;
				return;
			}

			var source = default(IWICBitmapSource);
			source = Frame = ctx.AddRef(ctx.Decoder.Decoder.GetFrame((uint)ctx.Settings.FrameIndex));

			if (ctx.Decoder.WicContainerFormat == Consts.GUID_ContainerFormatRaw && ctx.Settings.FrameIndex == 0 && ctx.Decoder.Decoder.TryGetPreview(out var preview))
				source = ctx.AddRef(preview);

			source.GetResolution(out double dpix, out double dpiy);
			DpiX = dpix;
			DpiY = dpiy;

			if (PixelFormat.Cache[source.GetPixelFormat()].NumericRepresentation == PixelNumericRepresentation.Indexed)
			{
				var pal = ctx.AddRef(Wic.Factory.CreatePalette());
				source.CopyPalette(pal);

				var newFormat = Consts.GUID_WICPixelFormat24bppBGR;
				if (pal.HasAlpha())
					newFormat = Consts.GUID_WICPixelFormat32bppBGRA;
				else if (pal.IsGrayscale() || pal.IsBlackWhite())
					newFormat = Consts.GUID_WICPixelFormat8bppGray;

				var conv = ctx.AddRef(Wic.Factory.CreateFormatConverter());
				conv.Initialize(source, newFormat, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
				source = conv;
			}

			if (source is IWICBitmapSourceTransform trans)
			{
				uint pw = 1, ph = 1;
				source.GetSize(out uint ow, out uint oh);
				trans.GetClosestSize(ref pw, ref ph);
				SupportsNativeScale = pw < ow || ph < oh;
				SupportsNativeTransform = trans.DoesSupportTransform(WICBitmapTransformOptions.WICBitmapTransformRotate270);
			}

			if (planar && source is IWICPlanarBitmapSourceTransform ptrans)
			{
				uint pw = 1, ph = 1;
				var pdesc = new WICBitmapPlaneDescription[2];
				var pfmts = new[] { Consts.GUID_WICPixelFormat8bppY, Consts.GUID_WICPixelFormat16bppCbCr };
				SupportsPlanarPipeline = ptrans.DoesSupportTransform(ref pw, ref ph, WICBitmapTransformOptions.WICBitmapTransformRotate0, WICPlanarOptions.WICPlanarOptionsDefault, pfmts, pdesc, 2);
			}

			bool preserveNative = SupportsPlanarPipeline || SupportsNativeTransform || (SupportsNativeScale && ctx.Settings.HybridScaleRatio > 1d);
			ctx.Source = source.AsPixelSource(nameof(IWICBitmapFrameDecode), !preserveNative);
		}
	}

	internal static class WicTransforms
	{
		private static IWICColorContext getDefaultColorProfile(Guid pixelFormat)
		{
			var pfi = Wic.Factory.CreateComponentInfo(pixelFormat) as IWICPixelFormatInfo;
			var cc = pfi.GetColorContext();
			Marshal.ReleaseComObject(pfi);
			return cc;
		}

		private static readonly Lazy<IWICColorContext> cmykProfile = new Lazy<IWICColorContext>(() => getDefaultColorProfile(Consts.GUID_WICPixelFormat32bppCMYK));
		private static readonly Lazy<IWICColorContext> srgbProfile = new Lazy<IWICColorContext>(() => getDefaultColorProfile(Consts.GUID_WICPixelFormat24bppBGR));

		public static void AddMetadataReader(WicProcessingContext ctx, bool basicOnly = false)
		{
			if (ctx.DecoderFrame.Frame == null)
				return;

			if (ctx.DecoderFrame.Frame.TryGetMetadataQueryReader(out var metareader))
			{
				ctx.AddRef(metareader);

				// Exif orientation
				if (metareader.TryGetMetadataByName("System.Photo.Orientation", out var pv))
				{
#pragma warning disable 0618 // VarEnum is obsolete
					if (pv.UnmanagedType == VarEnum.VT_UI2)
						ctx.DecoderFrame.ExifOrientation = (Orientation)Math.Min(Math.Max((ushort)Orientation.Normal, (ushort)pv.Value), (ushort)Orientation.Rotate270);
#pragma warning restore 0618

					var opt = ctx.DecoderFrame.ExifOrientation.ToWicTransformOptions();
					if (ctx.DecoderFrame.SupportsPlanarPipeline && opt != WICBitmapTransformOptions.WICBitmapTransformRotate0 && ctx.DecoderFrame.Frame is IWICPlanarBitmapSourceTransform ptrans)
					{
						uint pw = 1, ph = 1;
						var pdesc = new WICBitmapPlaneDescription[2];
						var pfmts = new[] { Consts.GUID_WICPixelFormat8bppY, Consts.GUID_WICPixelFormat16bppCbCr };
						ctx.DecoderFrame.SupportsPlanarPipeline = ptrans.DoesSupportTransform(ref pw, ref ph, opt, WICPlanarOptions.WICPlanarOptionsDefault, pfmts, pdesc, 2);
					}
				}

				if (basicOnly)
					return;

				// other requested properties
				var propdic = new Dictionary<string, PropVariant>();
				foreach (string prop in ctx.Settings.MetadataNames ?? Enumerable.Empty<string>())
				{
					if (metareader.TryGetMetadataByName(prop, out var pvar) && pvar.Value != null)
						propdic[prop] = pvar;
				}

				ctx.DecoderFrame.Metadata = propdic;
			}

			if (basicOnly)
				return;

			// ICC profiles
			//http://ninedegreesbelow.com/photography/embedded-color-space-information.html
			uint ccc = ctx.DecoderFrame.Frame.GetColorContextCount();
			var profiles = new IWICColorContext[ccc];
			var profile = default(IWICColorContext);

			if (ccc > 0)
			{
				for (int i = 0; i < ccc; i++)
					profiles[i] = ctx.AddRef(Wic.Factory.CreateColorContext());

				ctx.DecoderFrame.Frame.GetColorContexts(ccc, profiles);
			}

			foreach (var cc in profiles)
			{
				var cct = cc.GetType();
				if (cct == WICColorContextType.WICColorContextProfile)
				{
					uint ccs = cc.GetProfileBytes(0, null);
					var ccb = ArrayPool<byte>.Shared.Rent((int)ccs);
					cc.GetProfileBytes(ccs, ccb);

					var cp = new ColorProfileInfo(new ArraySegment<byte>(ccb, 0, (int)ccs));
					ArrayPool<byte>.Shared.Return(ccb);

					// match only color profiles that match our intended use. if we have a standard sRGB profile, don't save it; we don't need to convert
					if (cp.IsValid && ((cp.IsDisplayRgb && !cp.IsStandardSrgb) || (cp.IsCmyk && ctx.Source.Format.ColorRepresentation == PixelColorRepresentation.Cmyk) /* || (Context.IsGreyscale && cp.DataColorSpace == "GRAY") */))
					{
						profile = cc;
						break;
					}
				}
				else if (cct == WICColorContextType.WICColorContextExifColorSpace && cc.GetExifColorSpace() == ExifColorSpace.AdobeRGB)
				{
					profile = cc;
					break;
				}
			}

			ctx.SourceColorContext = profile ?? (ctx.Source.Format.ColorRepresentation ==	PixelColorRepresentation.Cmyk ? cmykProfile.Value : null);
			ctx.DestColorContext = srgbProfile.Value;
		}

		public static void AddConditionalCache(WicProcessingContext ctx)
		{
			if (!ctx.DecoderFrame.ExifOrientation.RequiresCache())
				return;

			var crop = ctx.Settings.Crop;
			var bmp = ctx.AddRef(Wic.Factory.CreateBitmapFromSourceRect(ctx.Source.WicSource, (uint)crop.X, (uint)crop.Y, (uint)crop.Width, (uint)crop.Height));
			ctx.Source = bmp.AsPixelSource(nameof(IWICBitmap));

			ctx.Settings.Crop = new Rectangle(0, 0, crop.Width, crop.Height);
		}

		public static void AddColorspaceConverter(WicProcessingContext ctx)
		{
			if (ctx.SourceColorContext == null)
				return;

			var trans = ctx.AddRef(Wic.Factory.CreateColorTransform());
			if (trans.TryInitialize(ctx.Source.WicSource, ctx.SourceColorContext, ctx.DestColorContext, ctx.Source.Format.FormatGuid))
				ctx.Source = trans.AsPixelSource(nameof(IWICColorTransform));
		}

		public static void AddPixelFormatConverter(WicProcessingContext ctx)
		{
			var oldFormat = ctx.Source.Format;
			if (oldFormat.ColorRepresentation == PixelColorRepresentation.Cmyk)
			{
				//TODO WIC doesn't support proper CMYKA conversion with color profile
				if (oldFormat.AlphaRepresentation == PixelAlphaRepresentation.None)
				{
					// WIC doesn't support 16bpc CMYK conversion with color profile
					if (oldFormat.BitsPerPixel == 64)
						ctx.Source = new FormatConversionTransform(ctx.Source, Consts.GUID_WICPixelFormat32bppCMYK);

					var trans = ctx.AddRef(Wic.Factory.CreateColorTransform());
					if (trans.TryInitialize(ctx.Source.WicSource, ctx.SourceColorContext, ctx.DestColorContext, Consts.GUID_WICPixelFormat24bppBGR))
					{
						ctx.Source = trans.AsPixelSource(nameof(IWICColorTransform));
						oldFormat = ctx.Source.Format;
					}
				}

				ctx.SourceColorContext = null;
			}

			var newFormat = Consts.GUID_WICPixelFormat24bppBGR;
			if (oldFormat.AlphaRepresentation != PixelAlphaRepresentation.None)
				newFormat = oldFormat.AlphaRepresentation == PixelAlphaRepresentation.Associated ? Consts.GUID_WICPixelFormat32bppPBGRA : Consts.GUID_WICPixelFormat32bppBGRA;
			else if (oldFormat.ColorRepresentation == PixelColorRepresentation.Grey)
				newFormat = Consts.GUID_WICPixelFormat8bppGray;

			if (oldFormat.FormatGuid == newFormat)
				return;

			var conv = ctx.AddRef(Wic.Factory.CreateFormatConverter());
			if (!conv.CanConvert(oldFormat.FormatGuid, newFormat))
				throw new NotSupportedException("Can't convert to destination pixel format");

			conv.Initialize(ctx.Source.WicSource, newFormat, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
			ctx.Source = conv.AsPixelSource(nameof(IWICFormatConverter));
		}

		public static void AddIndexedColorConverter(WicProcessingContext ctx)
		{
			var newFormat = Consts.GUID_WICPixelFormat8bppIndexed;

			if (!ctx.Settings.IndexedColor || ctx.Source.Format.NumericRepresentation == PixelNumericRepresentation.Indexed || ctx.Source.Format.ColorRepresentation == PixelColorRepresentation.Grey)
				return;

			var conv = ctx.AddRef(Wic.Factory.CreateFormatConverter());
			if (!conv.CanConvert(ctx.Source.Format.FormatGuid, newFormat))
				throw new NotSupportedException("Can't convert to destination pixel format");

			var bmp = ctx.AddRef(Wic.Factory.CreateBitmapFromSource(ctx.Source.WicSource, WICBitmapCreateCacheOption.WICBitmapCacheOnDemand));
			ctx.Source = bmp.AsPixelSource(nameof(IWICBitmap));

			var pal = ctx.AddRef(Wic.Factory.CreatePalette());
			pal.InitializeFromBitmap(ctx.Source.WicSource, 256u, ctx.Source.Format.AlphaRepresentation != PixelAlphaRepresentation.None);
			ctx.DestPalette = pal;

			conv.Initialize(ctx.Source.WicSource, newFormat, WICBitmapDitherType.WICBitmapDitherTypeErrorDiffusion, pal, 10.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
			ctx.Source = conv.AsPixelSource(nameof(IWICFormatConverter), false);
		}

		public static void AddExifRotator(WicProcessingContext ctx)
		{
			if (ctx.DecoderFrame.ExifOrientation == Orientation.Normal)
				return;

			var rotator = ctx.AddRef(Wic.Factory.CreateBitmapFlipRotator());
			rotator.Initialize(ctx.Source.WicSource, ctx.DecoderFrame.ExifOrientation.ToWicTransformOptions());
			ctx.Source = rotator.AsPixelSource(nameof(IWICBitmapFlipRotator), !ctx.DecoderFrame.SupportsPlanarPipeline && !ctx.DecoderFrame.SupportsNativeTransform);
		}

		public static void AddCropper(WicProcessingContext ctx)
		{
			if (ctx.Settings.Crop == new Rectangle(0, 0, (int)ctx.Source.Width, (int)ctx.Source.Height))
				return;

			var cropper = ctx.AddRef(Wic.Factory.CreateBitmapClipper());
			cropper.Initialize(ctx.Source.WicSource, ctx.Settings.Crop.ToWicRect());
			ctx.Source = cropper.AsPixelSource(nameof(IWICBitmapClipper));
		}

		public static void AddScaler(WicProcessingContext ctx, bool hybrid = false)
		{
			double rat = ctx.Settings.HybridScaleRatio;
			if ((ctx.Settings.Width == ctx.Source.Width && ctx.Settings.Height == ctx.Source.Height) || (hybrid && rat == 1d))
				return;

			if (ctx.Source.WicSource is IWICBitmapSourceTransform)
				ctx.Source = ctx.Source.WicSource.AsPixelSource(nameof(IWICBitmapFrameDecode));

			uint ow = hybrid ? (uint)Math.Ceiling(ctx.Source.Width / rat) : (uint)ctx.Settings.Width;
			uint oh = hybrid ? (uint)Math.Ceiling(ctx.Source.Height / rat) : (uint)ctx.Settings.Height;
			var mode = hybrid ? WICBitmapInterpolationMode.WICBitmapInterpolationModeFant :
			           ctx.Settings.Interpolation.WeightingFunction.Support < 0.1 ? WICBitmapInterpolationMode.WICBitmapInterpolationModeNearestNeighbor :
			           ctx.Settings.Interpolation.WeightingFunction.Support < 1.0 ? ctx.Settings.ScaleRatio > 1.0 ? WICBitmapInterpolationMode.WICBitmapInterpolationModeFant : WICBitmapInterpolationMode.WICBitmapInterpolationModeNearestNeighbor :
			           ctx.Settings.Interpolation.WeightingFunction.Support > 1.0 ? ctx.Settings.ScaleRatio > 1.0 ? WICBitmapInterpolationMode.WICBitmapInterpolationModeHighQualityCubic :WICBitmapInterpolationMode.WICBitmapInterpolationModeCubic :
			           ctx.Settings.ScaleRatio > 1.0 ? WICBitmapInterpolationMode.WICBitmapInterpolationModeFant : WICBitmapInterpolationMode.WICBitmapInterpolationModeLinear;

			var scaler = ctx.AddRef(Wic.Factory.CreateBitmapScaler());
			scaler.Initialize(ctx.Source.WicSource, ow, oh, mode);
			ctx.Source = scaler.AsPixelSource(nameof(IWICBitmapScaler));

			if (hybrid)
				ctx.Settings.Crop = new Rectangle(0, 0, (int)ctx.Source.Width, (int)ctx.Source.Height);
		}

		public static void AddNativeScaler(WicProcessingContext ctx)
		{
			double rat = ctx.Settings.HybridScaleRatio;
			if (rat == 1d || !ctx.DecoderFrame.SupportsNativeScale || !(ctx.Source.WicSource is IWICBitmapSourceTransform trans))
				return;

			uint ow = ctx.Source.Width, oh = ctx.Source.Height;
			uint cw = (uint)Math.Ceiling(ow / rat), ch = (uint)Math.Ceiling(oh / rat);
			trans.GetClosestSize(ref cw, ref ch);

			if (cw == ow && ch == oh)
				return;

			bool swap = ctx.DecoderFrame.ExifOrientation.SwapDimensions();
			double wrat = swap ? (double)oh / ch : (double)ow / cw;
			double hrat = swap ? (double)ow / cw : (double)oh / ch;

			var crop = ctx.Settings.Crop;
			ctx.Settings.Crop = new Rectangle(
				(int)Math.Floor(crop.X / wrat),
				(int)Math.Floor(crop.Y / hrat),
				Math.Min((int)Math.Ceiling(crop.Width / wrat), (int)(swap ? ch : cw)),
				Math.Min((int)Math.Ceiling(crop.Height / hrat), (int)(swap ? cw : ch))
			);

			var scaler = ctx.AddRef(Wic.Factory.CreateBitmapScaler());
			scaler.Initialize(ctx.Source.WicSource, cw, ch, WICBitmapInterpolationMode.WICBitmapInterpolationModeFant);
			ctx.Source = scaler.AsPixelSource(nameof(IWICBitmapSourceTransform));
		}

		public static void AddPlanarCache(WicProcessingContext ctx)
		{
			if (!(ctx.Source.WicSource is IWICPlanarBitmapSourceTransform trans))
				throw new NotSupportedException("Transform chain doesn't support planar mode.  Only JPEG Decoder, Rotator, Scaler, and PixelFormatConverter are allowed");

			double rat = ctx.Settings.HybridScaleRatio.Clamp(1d, 8d);
			uint width = (uint)Math.Ceiling(ctx.Source.Width / rat);
			uint height = (uint)Math.Ceiling(ctx.Source.Height / rat);

			var fmts = new[] { Consts.GUID_WICPixelFormat8bppY, Consts.GUID_WICPixelFormat16bppCbCr };
			var desc = new WICBitmapPlaneDescription[2];

			var opt = ctx.DecoderFrame.ExifOrientation.ToWicTransformOptions();
			if (!trans.DoesSupportTransform(ref width, ref height, opt, WICPlanarOptions.WICPlanarOptionsDefault, fmts, desc, 2))
				throw new NotSupportedException("Requested planar transform not supported");

			var cacheSource = ctx.AddDispose(new WicPlanarCache(trans, desc[0], desc[1], ctx.Settings.Crop.ToWicRect(), opt, width, height, rat));

			ctx.PlanarChromaSource = cacheSource.GetPlane(WicPlane.Chroma);
			ctx.PlanarLumaSource = cacheSource.GetPlane(WicPlane.Luma);
			ctx.Source = ctx.PlanarChromaSource;
			ctx.Source = ctx.PlanarLumaSource;
		}

		public static void AddPlanarConverter(WicProcessingContext ctx)
		{
			var conv = ctx.AddRef(Wic.Factory.CreateFormatConverter()) as IWICPlanarFormatConverter;
			conv.Initialize(new[] { ctx.PlanarLumaSource.WicSource, ctx.PlanarChromaSource.WicSource }, 2, Consts.GUID_WICPixelFormat24bppBGR, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
			ctx.Source = conv.AsPixelSource(nameof(IWICPlanarFormatConverter));
			ctx.PlanarChromaSource = ctx.PlanarLumaSource = null;
		}
	}
}