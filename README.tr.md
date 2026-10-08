# Corel AI Operatörü (Corel Sign Studio)

*English: [README.md](README.md)*

**CorelDRAW 2026**'yı COM otomasyonu üzerinden yöneten, .NET 8 / WPF ile yazılmış bir kontrol merkezi.

## Uygulama nedir, ne değildir?

- CorelDRAW'ın **yerine geçmez**. Tasarım alanınız CorelDRAW olmaya devam eder.
- Bir tabela tasarım programı değildir; tek bir iş türüne, sayfa boyutuna veya firmaya bağlı değildir.
- Açık belgeyi inceler, yapılacak işlemleri size **önce gösterir**, onayınızla CorelDRAW'da uygular.
- Bir kez başarıyla yapılan işi **otomasyon** olarak kaydedip yeni değerlerle tekrarlayabilir.

Serbest Türkçe cümleleri bir **yapay zekâ planlayıcısı** plana çevirir (API anahtarı gerekir). Anahtar yoksa
belirli komut kalıplarını anlayan **yerleşik test planlayıcısı** kullanılır.

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

## Yapay zekâ planlayıcısı

Ayarlar > **Yapay Zekâ** bölümünden sağlayıcıyı, modeli ve API anahtarını girip **Kaydet**'e, ardından
**Bağlantıyı Test Et**'e basın. Bundan sonra serbest Türkçe cümleler yazabilirsiniz:

```
Logoyu biraz küçült ve sağ üst köşeye al.
Başlığı ortala ve 10 mm yukarı taşı.
Üstteki başlığı PERSONEL GİRİŞİ olarak değiştir.
Seçtiğim nesneleri eşit aralıklarla yatay dağıt.
Bu sayfayı 50x70 cm yap.
```

- Yapay zekâ **yalnızca plan hazırlar**; CorelDRAW'a kendisi dokunmaz. Planı görür, **CORELDRAW'DA UYGULA**
  düğmesine siz basarsınız. Silme ve kaydetmeden kapatma için ayrıca onay istenir.
- Önce **Belgeyi İncele**'ye basın: yapay zekâ "logo", "başlık", "kırmızı daire" gibi ifadeleri belgedeki
  nesnelerin adı, metni, rengi ve konumundan bulur.
- Hangi nesneyi kastettiğiniz belirsizse rastgele seçmez, **soru sorar**; yanıtınızı yazıp yeniden
  **Planı Hazırla**'ya basarsınız.
- Yapay zekânın yanıtı uygulanmadan önce denetlenir: bilinmeyen işlem türü, tanınmayan alan, belgede
  olmayan nesne kimliği veya doğrulamadan geçmeyen plan reddedilir.
- API anahtarı Windows hesabınıza özel olarak şifrelenir (DPAPI) ve kullanıcı profilinizde saklanır;
  proje klasörüne, depoya veya günlüklere yazılmaz. İsterseniz `ANTHROPIC_API_KEY` ortam değişkenini
  kullanabilirsiniz.
- Yapay zekâ yapılandırılmamışsa veya ulaşılamıyorsa **yerleşik test planlayıcısı** devreye girer.
- İstek metni ile belgedeki nesnelerin adları, metinleri ve ölçüleri seçtiğiniz sağlayıcıya gönderilir.

## Referans görselinden yeniden oluşturma

Bir JPG, PNG, PDF, SVG veya CDR ekleyip örneğin şunu yazın:

```
Bunun aynısını 500x700 mm olarak CorelDRAW'da yap.
Bunun aynısını yap ama alttaki YASAKTIR yazısını GİRİLMEZ olarak değiştir.
Logoyu görsel olarak kullan ama diğer elemanları vektör olarak yeniden oluştur.
```

1. **Referansı Analiz Et** — görsel analiz edilir; **Referans Analizi** bölümünde bulunan nesneler, okunan
   metinler, güven düzeyi ve uyarılar gösterilir.
2. **Planı Hazırla** — analiz, düzenlenebilir CorelDRAW nesnelerinden oluşan bir plana çevrilir.
3. **CORELDRAW'DA UYGULA** — plan yeni bir belgede uygulanır.

Ne yapılır:

- Dikdörtgen, elips/daire, çizgi, düz kenarlı çokgen (üçgen, eşkenar dörtgen…), çerçeve ve **yasak işareti**
  gerçek CorelDRAW şekilleri olarak çizilir; metinler **düzenlenebilir metin**, tablolar **düzenlenebilir tablo** olur.
- Nesneler arkadan öne doğru oluşturulur; birbirine ait olanlar (ör. üç satırlık yazı bloğu) gruplanır.
  Belgenin tamamı tek grup yapılmaz.
- Türkçe ve Arapça metinler olduğu gibi korunur. Net okunamayan metin "emin değil" olarak işaretlenir.
- **Vektör dosyalar (SVG, CDR, tek sayfalı PDF) yeniden çizilmez**: kendi nesneleri içe aktarılır ve yapay
  zekâya gönderilmez. Çok sayfalı PDF'de hangi sayfa istendiği sorulur.
- **Logo ve karmaşık çizimler uydurulmaz.** Varlık kütüphanesinde eşleşen dosya varsa o kullanılır; "görsel
  olarak kullan" derseniz referanstan kesilen parça yerleştirilir; aksi hâlde pembe çerçeveli, adı
  "YER TUTUCU: …" olan bir kutu konur ve uyarı verilir.

Fiziksel ölçü kuralları:

- Piksel, milimetre değildir. Yazdığınız ölçü (`500x700 mm`, `50x70 cm`) her zaman önceliklidir.
- Ölçü yazmadıysanız PDF/SVG/CDR dosyasının kendi sayfa ölçüsü kullanılır.
- JPG/PNG'de ölçü yazmadıysanız ölçü **tahmin edilmez**, sorulur: "Bu tasarımın gerçek ölçüsü nedir?"

Neler gönderilir: yalnızca **Referansı Analiz Et**'e (veya yeniden oluşturma isteğiyle **Planı Hazırla**'ya)
bastığınızda, yalnızca seçtiğiniz referansın küçültülmüş bir kopyası yapay zekâ sağlayıcısına gönderilir.
Başka dosya gönderilmez; görsel içeriği günlüklere yazılmaz.

Sınırlar: fotoğraflar ve karmaşık illüstrasyonlar kusursuz vektöre çevrilemez. Bu özellik tabela, levha,
etiket, tablo, afiş gibi düzenli tasarımlar içindir. Yazı tipleri yaklaşık eşleştirilir ve bu açıkça belirtilir.
Sonucun referansla otomatik karşılaştırılıp düzeltilmesi henüz yoktur.

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

- Yerleşik planlayıcı yalnızca belirli test komutlarını anlar; serbest cümleler için yapay zekâ planlayıcısı (API anahtarı) gerekir.
- Yapay zekâ planlayıcısı görsel içeriği göremez: referans dosyalarının yalnızca adı ve türü iletilir.
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
