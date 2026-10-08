# Corel AI Operatörü (Corel Sign Studio)

*English: [README.md](README.md)*

**CorelDRAW 2026**'yı COM otomasyonu üzerinden yöneten, .NET 8 / WPF ile yazılmış bir kontrol merkezi.

## Uygulama nedir, ne değildir?

- CorelDRAW'ın **yerine geçmez**. Tasarım alanınız CorelDRAW olmaya devam eder.
- Bir tabela tasarım programı değildir; tek bir iş türüne, sayfa boyutuna veya firmaya bağlı değildir.
- Açık belgeyi inceler, yapılacak işlemleri size **önce gösterir**, onayınızla CorelDRAW'da uygular.
- Bir kez başarıyla yapılan işi **otomasyon** olarak kaydedip yeni değerlerle tekrarlayabilir.

Henüz harici bir yapay zekâ servisine bağlı değildir. Komutları şimdilik küçük bir **test planlayıcısı** anlar;
ileride yerini yapay zekâ planlayıcısı alacak ve uygulamanın geri kalanı değişmeyecek.

```
İstek ─► Planlayıcı ─► Plan (incelenebilir işlem listesi) ─► Uygulayıcı ─► CorelDRAW
Referans dosyası ─► Çözümleyici ─► Plan
Otomasyon + satırlar ─► her satır için bir plan ─► CDR / PDF / PNG / SVG
```

## Sekmeler

| Sekme | Ne işe yarar |
| --- | --- |
| **Operatör** | Referans dosyası ekleyin, ne yapılacağını yazın, **Planı Hazırla** ile işlemleri görün, **CORELDRAW'DA UYGULA** ile çalıştırın. |
| **Açık Belge** | CorelDRAW'daki belgenin nesneleri: kimlik, tür, ad, metin, konum, boyut, katman, grup. |
| **Otomasyonlar** | Kayıtlı otomasyonlar ve değişkenleri. Yeni değerlerle plan hazırlayın. |
| **Toplu İşler** | Bir otomasyonu CSV dosyasındaki her satır için çalıştırın. |
| **Varlıklar** | Logo, simge, işaret ve yeniden kullanılacak dosyalar için kütüphane. |
| **Geçmiş** | Uygulanan her plan ve sonucu; planı yeniden yükleyebilirsiniz. |
| **Ayarlar** | Çıktı klasörü, hata durumunda geri alma, veri klasörü, eski tabela aracı. |

## Örnek komutlar

Her satıra bir komut yazın. Nesne kimliklerini **Açık Belge** sekmesinden alın.

```
500x700 mm belge oluştur
Ortaya GİRİŞ YASAKTIR yaz
shape_001'i 10 mm sağa taşı
shape_001'i yüzde 20 büyüt
shape_001'i 45 derece döndür
shape_001 metnini MERHABA yap
shape_001'in rengini kırmızı yap
shape_001'i sayfanın ortasına hizala
Seçili nesneleri grupla
Seçili nesneleri 5 kere çoğalt
8 sütun 20 satır tablo oluştur
YeniKatman adında katman oluştur
Dosyayı C:\Cikti\is.cdr olarak kaydet
C:\Cikti\is.pdf olarak PDF dışa aktar
```

- Nesneyi `shape_012` kimliğiyle, CorelDRAW'da seçtiklerinizi `seçili nesneleri` diyerek, adı olan bir nesneyi
  çift tırnakla (`"Logo"yu sil`) gösterebilirsiniz.
- Türkçe ekler serbesttir: `shape_001'i`, `shape_001'ı`, `shape_001'in`, `shape_002'yi`, `shape_003'ün`.
- İngilizce komutlar da çalışmaya devam eder (`Move shape_001 10 mm right`).

## Otomasyonlar (reçeteler)

Başarılı bir planı **Otomasyon Olarak Kaydet** ile saklayın. Değişecek değerleri `AD = değer` biçiminde yazın:

```
KISI_ADI = Ahmet Yılmaz
GENISLIK_MM = 200
```

Daha sonra aynı düzeni yeni değerlerle (örneğin `KISI_ADI = Mehmet Kaya`) üretebilirsiniz. Otomasyonlar bir iş
türüne bağlı değildir: etiket, tablo, kartvizit, afiş, seri numaralı işler için aynı şekilde çalışır.

## Toplu işler

1. Bir otomasyon seçin.
2. İlk satırında değişken adları olan bir CSV dosyası seçin (Excel'in `;` ile kaydettiği dosyalar da okunur).
3. Çıktı türlerini (CDR, PDF, PNG, SVG) ve isterseniz çıktı adı şablonunu (`{{KISI_ADI}}_{{ROW}}`) belirleyin.
4. **Önizle** ile satırları kontrol edin, **TOPLU İŞLEMİ BAŞLAT** ile çalıştırın.

Var olan dosyaların üzerine yazılmaz; aynı ada sahip ikinci çıktı `_002` ekiyle kaydedilir.

## CorelDRAW bağlantısı ve güvenlik

- Uygulama, açık olan CorelDRAW'a bağlanır ve onu **kapatmaz**.
- Açık belgenizi değiştiren bir plan tek bir geri alma grubunda çalışır: başarılıysa tek **Ctrl+Z** ile geri
  alınır; bir adım başarısız olursa yapılan değişiklikler otomatik geri alınır.
- Nesne silen veya belge kapatan planlar önce onay ister.
- Ölçüler milimetredir; konumlar sayfanın sol üst köşesinden ölçülür.

## Dil ve yerelleştirme

- Varsayılan dil **Türkçe (tr-TR)**'dir: arayüz, durum ve hata mesajları, plan adımları, örnek komutlar.
- Metinler kodda değil, merkezi kaynak dosyalarındadır:
  - `CorelSignStudio.App/Localization/Ui.resx` — pencere metinleri (XAML'de `{local:Loc Anahtar}`).
  - `CorelSignStudio.Domain/Localization/Messages.resx` — plan adımları, doğrulama ve sonuç mesajları.
- İngilizce eklemek için `Ui.en-US.resx` ve `Messages.en-US.resx` dosyalarını ekleyip açılışta `Msg.Culture`
  değerini `en-US` yapmak yeterlidir.
- Sınıf/özellik adları, JSON alanları ve teknik günlük kayıtları bilerek İngilizce bırakılmıştır.

## Derleme ve test

```
dotnet build CorelSignStudio.sln
dotnet test  CorelSignStudio.sln                                # birim testleri; CorelDRAW gerekmez
$env:COREL_INTEGRATION = "1"; dotnet test CorelSignStudio.sln   # kurulu CorelDRAW 2026 ile de dener
dotnet run --project CorelSignStudio.App
```

## Bilinen sınırlamalar

- Yerleşik planlayıcı yalnızca belirli test komutlarını anlar; serbest cümleler için yapay zekâ planlayıcısı gerekir.
- JPG/PNG referanslar yalnızca görsel olarak içe aktarılır; düzenlenebilir vektöre çevirme henüz yoktur.
- Düzenlemeler etkin sayfada yapılır.
- PNG çıktısı sayfanın tamamını değil, sayfadaki çizimin kapladığı alanı içerir.
- Otomasyon değişkenleri formül içeremez; uyum sağlaması gereken yerleşimler için hizalama/dağıtma kullanın.
- Toplu iş verisi için yalnızca CSV desteklenir; Excel daha sonra eklenecektir.
- Dil, uygulama açılırken belirlenir; çalışırken değiştirilemez.
- Doğrulama mesajlarında alan adları (`WidthMm` gibi) teknik adlarıyla geçer.
- CorelDRAW meşgulse veya bir iletişim kutusu açıksa otomasyon o kutu kapatılana kadar bekler. CorelDRAW
  **deneme sürümünde** gizli örnekler deneme pencerelerine takılabilir; bu yüzden uygulama her zaman görünür
  bir CorelDRAW ile çalışır.
