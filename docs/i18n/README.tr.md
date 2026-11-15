# StashBay

> Pencereler arasında dosya aktarmak için hafif bir geçiş rafı: içine bırakın, dışarı sürükleyin ve bitti — pencereleri yan yana açmaya gerek yok.

[简体中文](../../README.md) · [English](../../README.en.md) · [繁體中文](README.zh-Hant.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Español](README.es.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [Português](README.pt.md) · [Русский](README.ru.md) · [Italiano](README.it.md) · [العربية](README.ar.md) · [हिन्दी](README.hi.md) · [বাংলা](README.bn.md) · [ไทย](README.th.md) · **Türkçe** · [Tiếng Việt](README.vi.md) · [Bahasa Indonesia](README.id.md)

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](../../LICENSE) [![365 Open Source Plan #048](https://img.shields.io/badge/365%20Open%20Source%20Plan-%23048-1f6feb)](https://github.com/rockbenben/365opensource)

[⬇ En Son Sürümü İndir](https://github.com/rockbenben/stashbay/releases/latest)

![StashBay kompakt raf: dosyaları tutmak ve taşımak için her zaman en üstte duran küçük bir kart rafı](../../assets/shelf.png)

Windows masaüstü için hafif, yerel bir dosya geçiş istasyonu. Buluta yükleme yapmadan, ağ istekleri olmadan ve hesap kaydı gerektirmeden tamamen çevrimdışı çalışır. Hem işinizi halledip kapatabileceğiniz minimalist bir raf hem de Git desteği, anlık görüntü yedeklemeleri ve tek tıklamayla geri alma sunan tam kapsamlı bir dosya çalışma tezgahı olarak işlev görür.

---

## Desteklenen Özellikler

| Boyut | Desteklenen Özellikler |
| :--- | :--- |
| **İşletim Sistemi** | Windows 10 (1809+) / Windows 11 (yerel x64 ve ARM64 desteği) |
| **Bağımlılıklar** | Resmi yükleyici ve taşınabilir sürümler çalışma ortamını içerir; hafif sürümler .NET 10 Desktop Runtime gerektirir |
| **Desteklenen Dosyalar** | Her formatta dosya ve çok katmanlı klasörler (göreli dizin yapısı korunur) |
| **Girdi Yöntemleri** | Dosya Gezgini seçimi, pano dosya listesi, fare sallama hareketi |
| **Geri Yazma Hedefleri** | Yerel diskler, ağ paylaşımları (UNC / eşlenmiş sürücüler), Git depoları |
| **Büyük Dosyalar** | 2 GB'den büyük tekil dosyaları ve Windows uzun yollarını (> 260 karakter) destekler |
| **İzinler** | Varsayılan olarak standart kullanıcı haklarıyla çalışır (`%LOCALAPPDATA%\Programs` dizinine kurulur); isteğe bağlı yönetici otomatik başlatma |
| **Veri Depolama** | Varsayılan olarak `%LOCALAPPDATA%\StashBay\` dizininde saklanır (eski `FileStash` klasörüyle tam uyumludur) |

---

## Hızlı Başlangıç

1. **Programı İndirin**: [Releases](https://github.com/rockbenben/stashbay/releases/latest) sayfasından `StashBay-vx.y.z-setup.exe` yükleyicisini (yönetici izni gerektirmez) veya taşınabilir `StashBay-vx.y.z-win-x64.zip` paketini indirin.
2. **Rafı Açın**: <kbd>Win+Alt+S</kbd> kısayoluna basın; mini raf etkin ekranda fare imlecinizin hemen yanında açılır.
3. **Dosyaları Bırakın**: Dosya Gezgini'nde dosyaları seçin ve tekrar <kbd>Win+Alt+S</kbd> tuşuna basın (veya rafa sürükleyin ya da fareyi hafifçe sallayın) — dosyalar anında rafa yerleşir.
4. **Hepsini Tek Hamlede Çıkarın**: Alt kısımdaki `[⤹ Toplam X Öğe]` tutamacından sürükleyerek tüm dosyaları hedef pencereye veya sohbet uygulamasına tek seferde aktarın. Tüm dosyalar çıkarıldığında raf otomatik olarak kapanır.

---

## Temel Özellikler

### Minimalist Geçiş Rafı (DropPoint Deneyimi)
* **Tek Seferde Toplu Dışa Aktarma**: Tüm dosyaları Gezgin'e, sohbet uygulamalarına veya web formlarına tek bir hareketle taşımak için alttaki sayaç tutamacını sürükleyin.
* **Boşalınca Otomatik Kapanma**: Dosyalar hedefe bırakıldığında raftan çıkarılır; raf tamamen boşaldığında pencere kendiliğinden kaybolur.
* **İmleç Konumunda Açılma**: Genel kısayol farenin bulunduğu monitörü algılar ve rafı doğrudan orada açar.
* **Sallayarak Dosya Ekleme**: Gezgin'de dosyaları sürüklerken fareyi hafifçe sağa sola sallayarak rafı açabilir ve dosyaları olduğu yerde saklayabilirsiniz.
* **Manyetik Kayan Buton**: Ekran kenarında duran, tıklandığında veya hafifçe sürüklendiğinde açılan ve bırakıldığında ekran kenarına yapışan küçük bir buton.
* **Bağımsız Çoklu Raflar**: Tepsi simgesine çift tıklayarak veya <kbd>Ctrl+Shift+N</kbd> tuşlarına basarak yeni geçici raflar açabilirsiniz; boşalan geçici raflar otomatik temizlenir.

### Git Dalı Değişikliklerini Saklama ve Güvenli Geri Yükleme
* **Depo Algılama**: Git depolarındaki dosyaların kök dizinini hatırlar; dal değiştirdiğinizde tek tıklamayla orijinal konumuna geri yükleme bildirimi gösterir.
* **Yazmadan Önce Kilit Kontrolü**: Dosyaların üzerine yazmadan önce Office veya IDE gibi uygulamalar tarafından kilitlenip kilitlenmediğini Windows Yeniden Başlatma Yöneticisi ile denetler.
* **Üzerine Yazmadan Önce Güvenlik Anlık Görüntüsü**: Dosyaların üzerine yazılmadan önce yerel Blob deposuna otomatik yedek alınır ve tek tıklamayla geri alınabilir.
* **Karma Karşılaştırması ile Çiftleri Atlama**: İçerik tamamen aynıysa yazma işlemini atlar, orijinal değiştirilme tarihini korur ve gereksiz derlemeleri önler.

### Tam Kapsamlı Çalışma Tezgahı
* **Dört Görünüm Modu**: Ağaç görünümü, düz liste, kaynak klasöre göre gruplandırılmış ve büyük simgeli döşemeler; görünüm durumu hafızada tutulur.
* **Esnek Geri Yükleme Modları**: Orijinal konum, belirtilen dizin veya yeni kök eşleme; tam göreli yol, yalnızca dosya adı veya düzleştirilmiş yapı.
* **Çakışma Önizlemesi**: Uygulamadan önce ayrıntıları inceleyin: Sor, Üzerine Yaz, Atla veya Otomatik Yeniden Adlandır.
* **Toplu İşlem Geçmişi**: Her geri yükleme işlemi için ayrıntılı günlük; kısmi hatalarda yalnızca başarısız olanları yeniden deneyebilirsiniz.

### 18 Yerel Dil Desteği ve Temalar
* 18 dilde tam yerel destek (testlerle %100 doğrulanan anahtar eşleşmesi) ve Arapça (ar-SA) için tam RTL ayna düzeni.
* Windows'un koyu ve açık modlarına kusursuz uyum sağlayan nötr grafit renk paleti.

---

## Sık Kullanılan Kısayollar

| Kısayol | Kapsam | Açıklama |
| :--- | :--- | :--- |
| <kbd>Win+Alt+S</kbd> | Genel | Mini rafı açar; Gezgin'de seçili dosya varsa doğrudan rafa ekler |
| <kbd>Win+Alt+W</kbd> | Genel | Tam çalışma tezgahını açar |
| <kbd>Win+Alt+V</kbd> | Genel | Panodaki dosyaları doğrudan rafa ekler |
| <kbd>Ctrl+V</kbd> | Pencere içi | Panodaki dosyaları aktif rafa yapıştırır |
| <kbd>Ctrl+Shift+N</kbd> | Pencere içi | Yeni bağımsız boş bir raf oluşturur |
| <kbd>F11</kbd> / <kbd>Ctrl+E</kbd> | Pencere içi | Mini raf ile tam tezgah arasında geçiş yapar |
| <kbd>Esc</kbd> | Pencere içi | Tezgahta rafa küçültür; rafta tepsiye gizler |
| <kbd>Delete</kbd> | Liste | Seçili öğeleri raftan kaldırır |

> [!TIP]
> Tüm genel kısayollar ayarlardan özelleştirilebilir veya kapatılabilir. Bir kısayol başka bir programla çakışırsa StashBay açık bir uyarı gösterir.

---

## Komut Satırı Parametreleri (CLI)

`StashBay.exe`, betikler ve otomasyon araçlarıyla entegrasyon için zengin komut satırı seçenekleri sunar:

| Seçenek | Açıklama |
| :--- | :--- |
| `--add <yollar...>` | Rafa bir veya daha fazla dosya/klasör ekler |
| `--group <ad>` | Hedef rafı belirtmek için `--add` ile kullanılır (yoksa oluşturulur) |
| `--apply-here <dizin>` | Raftaki dosyaları belirtilen dizine yazar |
| `--apply-original` | Dosyaları orijinal göreli yollarına geri yükler (Git için ideal) |
| `--show` | Ana pencereyi ön plana çıkarır ve görüntüler |
| `--minimized` | Sistem tepsisinde simge durumuna küçülmüş olarak başlatır (Windows otomatik başlatma için) |
| `--quit` | Çalışan örneği güvenli bir şekilde kapatır |
| `--unregister-shell` | Sağ tık menüsü, Gönder kısayolu ve otomatik başlatmayı kaldırır |

---

## Bilinen Sınırlamalar

* **Yalnızca Windows İçin**: Windows Shell, DWM bileşimi ve erişim denetimi ile derinlemesine entegredir; diğer işletim sistemleri için bir sürüm planlanmamaktadır.
* **Kısayol Değil Anlık Görüntü**: Dosyalar eklenirken içeriğin kopyası anında oluşturulur; orijinal dosyaları değiştirmek raftaki kopyayı etkilemez.
* **Tek Örnek Mekanizması**: Uygulamayı tekrar çalıştırmak parametreleri tepside çalışan mevcut işleme iletir.
* **Yönetici Otomatik Başlatma**: Standart izinlerle çalışır. Yönetici ayrıcalıklı pencerelere dosya sürüklemeniz gerekiyorsa ayarlardan yönetici başlangıcını açıp UAC onayını verin. [Otomatik Başlatma Kılavuzu](../admin-startup.md).

---

## 365 Açık Kaynak Planı hakkında

[365 Açık Kaynak Planı](https://github.com/rockbenben/365opensource) kapsamındaki **#048** numaralı proje — bir kişi + yapay zeka, bir yılda 300'den fazla açık kaynak proje.

[Fikrinizi paylaşın →](https://365.aishort.top/) · [Discord](https://discord.gg/PZTQfJ4GjX) · [Telegram](https://t.me/aishort_top)
