namespace Phyphox.Camera;

/// <summary>Pure BGR8 CPU analysis of the supplied ROI. No camera is opened and no native library is required.</summary>
public static class CameraAnalysis
{
    private static readonly double[] Linear=Enumerable.Range(0,256).Select(x=>Linearize(x/255d)).ToArray();
    public static CameraRoi ResolveRoi(NormalizedCameraRoi roi,int width,int height) {
        if(width<1||height<1||new[]{roi.X1,roi.Y1,roi.X2,roi.Y2}.Any(x=>!double.IsFinite(x)||x<0||x>1)||roi.X1==roi.X2||roi.Y1==roi.Y2)throw new ArgumentException("Normalized ROI must be non-empty within 0..1.");
        int x=(int)Math.Floor(Math.Min(roi.X1,roi.X2)*width),y=(int)Math.Floor(Math.Min(roi.Y1,roi.Y2)*height);
        int right=Math.Min(width,(int)Math.Ceiling(Math.Max(roi.X1,roi.X2)*width)),bottom=Math.Min(height,(int)Math.Ceiling(Math.Max(roi.Y1,roi.Y2)*height));
        return new(x,y,right-x,bottom-y);
    }
    public static double Linearize(double value)=>value<0.04045?value/12.92:Math.Pow((value+0.055)/1.055,2.4);
    public static CameraAnalysisResult AnalyzeBgr(ReadOnlySpan<byte> pixels,int width,int height,int stride,CameraRoi? roi=null,SpectrumAxis axis=SpectrumAxis.Horizontal,ExposureMetadata? exposure=null) {
        if(width<1||height<1||stride<(long)width*3||pixels.Length<(long)stride*height)throw new ArgumentException("Invalid BGR frame dimensions/stride.");
        var r=roi??new CameraRoi(0,0,width,height);
        if(r.X<0||r.Y<0||r.Width<1||r.Height<1||(long)r.X+r.Width>width||(long)r.Y+r.Height>height)throw new ArgumentException("ROI must be non-empty and within the actual frame.");
        int bins=axis==SpectrumAxis.Horizontal?r.Width:r.Height;var spectrum=new double[bins];var positions=new double[bins];
        double red=0,green=0,blue=0,luma=0,luminance=0,saturation=0,value=0,hx=0,hy=0;
        for(int y=r.Y;y<r.Y+r.Height;y++)for(int x=r.X;x<r.X+r.Width;x++) {
            int offset=y*stride+x*3;int b=pixels[offset],g=pixels[offset+1],rv=pixels[offset+2];
            double rf=rv/255d,gf=g/255d,bf=b/255d;red+=rf;green+=gf;blue+=bf;
            luma+=.2126*rf+.7152*gf+.0722*bf;
            double lum=.2126*Linear[rv]+.7152*Linear[g]+.0722*Linear[b];luminance+=lum;
            int bin=axis==SpectrumAxis.Horizontal?x-r.X:y-r.Y;spectrum[bin]+=lum;
            double max=Math.Max(rf,Math.Max(gf,bf)),min=Math.Min(rf,Math.Min(gf,bf)),delta=max-min;
            double h=delta==0?0:max==rf?(gf-bf)/delta+(gf<bf?6:0):max==gf?(bf-rf)/delta+2:(rf-gf)/delta+4;
            h*=Math.PI/3;hx+=Math.Cos(h);hy+=Math.Sin(h);saturation+=max==0?0:delta/max;value+=max;
        }
        double count=(long)r.Width*r.Height;double hue=Math.Atan2(hy,hx)*180/Math.PI;if(hue<0)hue+=360;
        for(int i=0;i<bins;i++){positions[i]=i+(axis==SpectrumAxis.Horizontal?r.X:r.Y);spectrum[i]/=axis==SpectrumAxis.Horizontal?r.Height:r.Width;}
        double? factor=null;
        if(exposure!=null) {
            if(!double.IsFinite(exposure.ApertureValue)||!double.IsFinite(exposure.Iso)||exposure.Iso<=0||!double.IsFinite(exposure.ShutterNanoseconds)||exposure.ShutterNanoseconds<=0)throw new ArgumentException("Exposure metadata must contain finite physical ISO/shutter values.");
            factor=Math.Pow(2,exposure.ApertureValue)/2*100/exposure.Iso*(1e9/60)/exposure.ShutterNanoseconds;
            if(!double.IsFinite(factor.Value))throw new ArgumentException("Exposure correction overflows.");
        }
        return new(red/count,green/count,blue/count,luma/count,luminance/count,hue,saturation/count,value/count,factor*luminance/count,positions,spectrum,factor.HasValue?spectrum.Select(x=>x*factor.Value).ToArray():null);
    }
}
